using Microsoft.Win32;
using SoundProtection.Core;

namespace SoundProtection.App;

/// <summary>
/// Reads the registry facts that decide whether the plug-in is in place. It only reads (every key is opened read-only)
/// and it is called from a background thread, never the UI thread. The value names are the ones in native/apo/SpShared.h
/// and the installer.
/// </summary>
internal static class ProtectionRegistry
{
    private const string RenderPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    private const string AudioPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio";
    private const string TestKeyValue = "DisableProtectedAudioDG";

    private const string NameValue = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    private const string StreamEffectsValue = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13";   // per-stream list: the Leveler goes here
    private const string EndpointEffectsValue = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15"; // end-of-line list: the Safety Cap goes here
    private const string EffectsDisabledValue = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
    private const string ExclusiveAllowedValue = "{b3f8fa53-0004-438e-9003-51a46e139bfc},3";

    private const string LevelerClsid = "026BF7A9-ED1A-4C62-ADC6-142802421CBA";
    private const string SafetyCapClsid = "148D0A3C-621B-40F5-B7FD-13E6CB834BCB";

    private const string HeadphonesName = "Headphones";
    private const int DeviceStateMask = 0xF;
    private const int DeviceStateNotPresent = 0x4;

    /// <summary>
    /// The test key, and the output devices that have at least one of our two effects listed or are named Headphones.
    /// Devices Windows reports as not present are left out: a leftover entry cannot play sound and must not count as protected.
    /// Throws if the registry cannot be read, so that the caller can keep what it knew before.
    /// </summary>
    public static RegistryFacts Read()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var endpoints = new List<EndpointFacts>();
        using (var render = machine.OpenSubKey(RenderPath, writable: false))
        {
            if (render is not null)
            {
                foreach (var id in render.GetSubKeyNames())
                {
                    if (ReadEndpoint(render, id) is { } facts)
                    {
                        endpoints.Add(facts);
                    }
                }
            }
        }

        using var audio = machine.OpenSubKey(AudioPath, writable: false);
        var testKeyOn = audio?.GetValue(TestKeyValue) is int value && value == 1;
        return new RegistryFacts(testKeyOn, endpoints);
    }

    private static EndpointFacts? ReadEndpoint(RegistryKey render, string id)
    {
        using var endpoint = render.OpenSubKey(id, writable: false);
        if (endpoint is null)
        {
            return null; // removed while we were reading
        }

        if (endpoint.GetValue("DeviceState") is int state && (state & DeviceStateMask) == DeviceStateNotPresent)
        {
            return null;
        }

        using var properties = endpoint.OpenSubKey("Properties", writable: false);
        using var fx = endpoint.OpenSubKey("FxProperties", writable: false);

        var levelerListed = Lists(fx, StreamEffectsValue, LevelerClsid) || Lists(properties, StreamEffectsValue, LevelerClsid);
        var safetyCapListed = Lists(fx, EndpointEffectsValue, SafetyCapClsid) || Lists(properties, EndpointEffectsValue, SafetyCapClsid);
        var name = properties?.GetValue(NameValue) as string;
        var named = string.Equals(name?.Trim(), HeadphonesName, StringComparison.OrdinalIgnoreCase);
        if (!levelerListed && !safetyCapListed && !named)
        {
            return null;
        }

        var effectsDisabled = IsOne(fx?.GetValue(EffectsDisabledValue)) || IsOne(properties?.GetValue(EffectsDisabledValue));
        var exclusive = properties?.GetValue(ExclusiveAllowedValue) ?? fx?.GetValue(ExclusiveAllowedValue);
        return new EndpointFacts(
            string.IsNullOrWhiteSpace(name) ? id : name.Trim(),
            levelerListed,
            safetyCapListed,
            effectsDisabled,
            ExclusiveAllowed: exclusive is null || !IsZero(exclusive), // not set means allowed, which is the Windows default
            EndpointId: id); // the key name, like {ab14dbc4-...}: the end of the output's Core Audio id
    }

    // A list of effect classes is a multi-string; one effect alone may be stored as a plain string.
    private static bool Lists(RegistryKey? key, string valueName, string clsid) =>
        key?.GetValue(valueName) switch
        {
            string[] items => items.Any(item => IsClsid(item, clsid)),
            string single => IsClsid(single, clsid),
            _ => false,
        };

    private static bool IsClsid(string text, string clsid) =>
        string.Equals(text.Trim().Trim('{', '}'), clsid, StringComparison.OrdinalIgnoreCase);

    private static bool IsOne(object? value) => value is int i && i == 1;

    private static bool IsZero(object value) => value is int i && i == 0;
}
