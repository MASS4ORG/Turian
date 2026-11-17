using System.Xml;
using System.Xml.Linq;

namespace Turian.Editor.Core;

/// <summary>Writes component test results in the JUnit format accepted by CI report viewers.</summary>
public static class SceneTestJUnit
{
    /// <summary>Creates or replaces a report, preserving failure details and source locations.</summary>
    public static void Write(SceneTestReport report, string path)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var suite = new XElement("testsuite", new XAttribute("name", "Turian component tests"),
            new XAttribute("tests", report.Tests.Count),
            new XAttribute("failures", report.Tests.Count(static test => test.Status == SceneTestStatus.Failed)),
            new XAttribute("skipped", report.Tests.Count(static test => test.Status == SceneTestStatus.NotRun)),
            new XAttribute("time", Seconds(report.Tests.Sum(static test => test.Seconds))));
        suite.Add(report.Tests.Select(Case));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        new XDocument(new XElement("testsuites", suite)).Save(path);
    }

    static XElement Case(SceneTestResult test)
    {
        var item = new XElement("testcase", new XAttribute("name", Text(test.Name)),
            new XAttribute("classname", Text(test.Name[..Math.Max(0, test.Name.LastIndexOf('.'))])),
            new XAttribute("time", Seconds(test.Seconds)));
        if (test.SourceFile is not null)
        {
            item.Add(new XAttribute("file", Text(test.SourceFile)), new XAttribute("line", test.SourceLine));
        }
        if (test.Status == SceneTestStatus.Failed)
        {
            item.Add(new XElement("failure", new XAttribute("message", Text(test.Message ?? "Test failed")),
                Text(test.StackTrace ?? test.Message)));
        }
        if (test.Status == SceneTestStatus.NotRun) { item.Add(new XElement("skipped")); }
        return item;
    }

    static string Text(string? value)
    {
        var text = new StringBuilder();
        foreach (var rune in (value ?? string.Empty).EnumerateRunes())
        {
            if (rune.Value > char.MaxValue || XmlConvert.IsXmlChar((char)rune.Value)) { text.Append(rune.ToString()); }
            else { text.Append("\\u").Append(rune.Value.ToString("X4", CultureInfo.InvariantCulture)); }
        }
        return text.ToString();
    }

    static string Seconds(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
