using System.Globalization;
using System.Runtime.Versioning;
using DesktopAutoAI.Uia;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Serilog;

namespace DesktopAutoAI.Agent;

public sealed record VerifyResult(bool Ok, string Reason);

/// <summary>
/// Checks an <see cref="Expectation"/> against the live UIA tree by reading
/// control state back through FlaUI patterns. No LLM involved - this is what
/// makes batch mode accurate: an action that silently no-ops is caught here
/// instead of being assumed to have worked.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Verifier
{
    private readonly AutomationElement _root;

    public Verifier(AutomationElement targetWindowRoot)
    {
        _root = targetWindowRoot;
    }

    public VerifyResult Check(Expectation expect)
    {
        var check = (expect.Check ?? ExpectationChecks.None).Trim().ToLowerInvariant();
        if (check == ExpectationChecks.None) return Ok();

        // exists / gone resolve the selector and assert presence/absence.
        if (check == ExpectationChecks.Exists)
        {
            if (expect.Selector is null) return Fail("exists check has no selector.");
            return Resolve(expect.Selector) is not null
                ? Ok()
                : Fail($"expected element to exist but it was not found: {Describe(expect.Selector)}");
        }
        if (check == ExpectationChecks.Gone)
        {
            if (expect.Selector is null) return Fail("gone check has no selector.");
            return Resolve(expect.Selector) is null
                ? Ok()
                : Fail($"expected element to be gone but it is still present: {Describe(expect.Selector)}");
        }

        // The remaining checks need a live element to read state from.
        if (expect.Selector is null) return Fail($"{check} check has no selector.");
        var el = Resolve(expect.Selector);
        if (el is null) return Fail($"target for {check} not found: {Describe(expect.Selector)}");

        return check switch
        {
            ExpectationChecks.ValueEquals => CheckValue(el, expect.Value),
            ExpectationChecks.ToggleOn => CheckToggle(el, ToggleState.On),
            ExpectationChecks.ToggleOff => CheckToggle(el, ToggleState.Off),
            ExpectationChecks.Selected => CheckSelected(el),
            ExpectationChecks.RangeEquals => CheckRange(el, expect.Value),
            _ => Fail($"unknown expectation check '{check}'."),
        };
    }

    /// <summary>
    /// Default postcondition when the model didn't supply one. Kept lenient: a
    /// mismatch is reported but treated as a soft warning by the caller, so we
    /// don't trigger spurious repairs on controls that reformat their value.
    /// </summary>
    public VerifyResult CheckDefault(AgentAction action)
    {
        if (action.Type == ActionTypes.Type && action.Selector is not null && !string.IsNullOrEmpty(action.Text))
        {
            var el = Resolve(action.Selector);
            if (el is null) return Fail("typed-into element not found on readback.");
            return CheckValue(el, action.Text);
        }
        return Ok();
    }

    private AutomationElement? Resolve(ActionSelector selector)
    {
        try { return UiaElementResolver.Resolve(_root, selector); }
        catch (Exception ex)
        {
            Log.Debug(ex, "Verifier resolve threw; treating as not found.");
            return null;
        }
    }

    private static VerifyResult CheckValue(AutomationElement el, string? expected)
    {
        if (expected is null) return Fail("value_equals check has no value.");
        if (!el.Patterns.Value.IsSupported)
            return Fail("target does not support the Value pattern.");
        var actual = el.Patterns.Value.Pattern.Value.ValueOrDefault ?? "";
        return Normalize(actual) == Normalize(expected)
            ? Ok()
            : Fail($"value mismatch: expected '{expected}', got '{actual}'.");
    }

    private static VerifyResult CheckToggle(AutomationElement el, ToggleState want)
    {
        if (!el.Patterns.Toggle.IsSupported)
            return Fail("target does not support the Toggle pattern.");
        var state = el.Patterns.Toggle.Pattern.ToggleState.ValueOrDefault;
        return state == want
            ? Ok()
            : Fail($"toggle state mismatch: expected {want}, got {state}.");
    }

    private static VerifyResult CheckSelected(AutomationElement el)
    {
        if (!el.Patterns.SelectionItem.IsSupported)
            return Fail("target does not support the SelectionItem pattern.");
        return el.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault
            ? Ok()
            : Fail("element is not selected.");
    }

    private static VerifyResult CheckRange(AutomationElement el, string? expected)
    {
        if (expected is null) return Fail("range_equals check has no value.");
        if (!double.TryParse(expected, NumberStyles.Any, CultureInfo.InvariantCulture, out var want))
            return Fail($"range_equals value '{expected}' is not a number.");
        if (!el.Patterns.RangeValue.IsSupported)
            return Fail("target does not support the RangeValue pattern.");
        var actual = el.Patterns.RangeValue.Pattern.Value.ValueOrDefault;
        return Math.Abs(actual - want) < 0.0001
            ? Ok()
            : Fail($"range value mismatch: expected {want}, got {actual}.");
    }

    private static string Normalize(string s) => s.Trim();

    private static string Describe(ActionSelector s)
        => s.AutomationId is { Length: > 0 } id ? $"automation_id='{id}'"
         : s.Name is { Length: > 0 } n ? $"name='{n}'"
         : s.Path is { Count: > 0 } p ? $"path[{p.Count} hops]"
         : "(empty selector)";

    private static VerifyResult Ok() => new(true, "ok");
    private static VerifyResult Fail(string reason) => new(false, reason);
}
