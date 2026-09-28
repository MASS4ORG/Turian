namespace Turian.Tests;

/// <summary>Tests for XLIFF import of string tables.</summary>
public class LocalizationExchangeXliffTests
{
    /// <summary>A table written as XLIFF reads back with the same entries.</summary>
    [Fact]
    public void WrittenTableReadsBack()
    {
        var table = new StringTable("pt-BR");
        table.Add(new StringTableEntry
        { Key = "menu.play", Source = "Play", Translation = "Jogar", Note = "Button", State = "final" });
        table.Add(new StringTableEntry { Key = "menu.quit", Source = "Quit" });

        var read = LocalizationExchange.ReadXliff(LocalizationExchange.WriteXliff(table));

        Assert.Equal("pt-BR", read.Locale);
        Assert.True(read.TryGet("menu.play", out var play));
        Assert.Equal(("Play", "Jogar", "Button", "final"), (play.Source, play.Translation, play.Note, play.State));
        Assert.True(read.TryGet("menu.quit", out var quit));
        Assert.Equal((null, "new"), (quit.Translation, quit.State));
    }

    /// <summary>Namespaced files, id-less units, empty targets and a source-only language all read.</summary>
    [Fact]
    public void ReadsNamespacedFileWithFallbacks()
    {
        const string xml = """
            <xliff xmlns="urn:oasis:names:tc:xliff:document:1.2" version="1.2">
              <file source-language="fr"><body>
                <trans-unit><source>Bonjour</source><target state="translated"></target></trans-unit>
              </body></file>
            </xliff>
            """;

        var read = LocalizationExchange.ReadXliff(xml);

        Assert.Equal("fr", read.Locale);
        Assert.True(read.TryGet("Bonjour", out var entry));
        Assert.Null(entry.Translation);
        Assert.Equal("translated", entry.State);
    }

    /// <summary>A document without a file element is rejected.</summary>
    [Fact]
    public void MissingFileElementThrows() =>
        Assert.Throws<InvalidDataException>(() => LocalizationExchange.ReadXliff("<xliff/>"));
}
