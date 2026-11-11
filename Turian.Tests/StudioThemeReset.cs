using Xunit.v3;

[assembly: Turian.Tests.StudioThemeReset]

namespace Turian.Tests;

/// <summary>
/// Puts the default Studio theme back after every test. A workbench applies its appearance to the process-wide
/// <see cref="ThemeTokens.Current"/>, text size and zoom included, so without this a layout test passed or failed with
/// whichever test ran before it, and the order follows the checkout path. Tests that change the theme run in the serial
/// collection, alone, so restoring it never races a running test.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class StudioThemeReset : BeforeAfterTestAttribute
{
    /// <inheritdoc/>
    public override void After(MethodInfo methodUnderTest, IXunitTest test) => ThemeTokens.Current = ThemeTokens.Default;
}
