namespace Turian.Editor.CLI;

public static partial class Program
{
    static Command ThemeCommand() => new("theme", "Check studio theme sheets (.pss) before installing them")
    {
        ThemeVerifyCommand(),
    };

    static Command ThemeVerifyCommand()
    {
        var pathArg = new Argument<string>("path") { Description = "A .pss file, a folder of them, or a theme brick folder" };
        var command = new Command("verify",
            "Parse and resolve every sheet, report unknown or mistyped tokens, and warn about low text contrast")
        {
            pathArg,
        };
        command.SetAction(result =>
        {
            IReadOnlyList<Gaya.Host.ThemeFinding> findings;
            try
            {
                findings = Gaya.Host.ThemeVerifier.Verify(result.GetValue(pathArg)!);
            }
            catch (FileNotFoundException ex)
            {
                Console.Error.WriteLine($"{ex.FileName}: {ex.Message}");
                return 2;
            }

            foreach (var finding in findings) Console.WriteLine(finding);
            var errors = findings.Count(f => f.Severity == Gaya.Host.ThemeFindingSeverity.Error);
            Console.WriteLine(errors == 0 ? "Theme sheets are valid" : $"{errors} error(s)");
            return errors == 0 ? 0 : 1;
        });
        return command;
    }
}
