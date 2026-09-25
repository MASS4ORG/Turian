namespace Turian.Tests;

/// <summary>Tests for <see cref="UiXmlParser"/> — the <c>.ui</c> XML → <see cref="UiDocument"/> parser.</summary>
public sealed class UiXmlParserTests
{
    const string doc = """
        <?xml version="1.0" encoding="utf-8"?>
        <UI xmlns="https://turian.mass4.org/ui"
            xmlns:x="https://example.com/x"
            controller="Usercode.MainMenu">

          <Style src="Assets/UI/theme.uss" />
          <Style src="menu.uss" />

          <Template name="MenuButton">
            <Button name="btn" class="menu-item" text="{label}" click="OnPick" />
          </Template>

          <VisualElement name="root" class="screen dark"
                         style="flex-direction: column; gap: 12">
            <Label name="title" class="h1" text="{App.Title}" tooltip="the title">Fallback Title</Label>

            <ScrollView>
              <Repeat items="{App.Levels}" as="level">
                <Instance template="MenuButton" label="{level.Name}" />
              </Repeat>
            </ScrollView>

            <Button name="play" text="Play" click="OnPlay" binding-enabled="{App.CanPlay}" />
            <x:Gauge name="fps" value="{App.Fps}" />

            <Bindings>
              <Binding element="title" property="text" path="App.Title" mode="OneWay" />
              <Binding element="play" property="enabled" path="App.CanPlay" converter="not" />
            </Bindings>
          </VisualElement>
        </UI>
        """;

    /// <summary>The full document parses into the expected shape.</summary>
    [Fact]
    public void Parse_FullDocument()
    {
        var parsed = UiXmlParser.Parse(doc, "menu.ui");

        Assert.Equal("Usercode.MainMenu", parsed.ControllerType);
        Assert.Equal(new[] { "Assets/UI/theme.uss", "menu.uss" }, parsed.StyleSheets);
        Assert.Equal("menu.ui", parsed.SourcePath);

        Assert.True(parsed.Templates.ContainsKey("MenuButton"));
        Assert.Equal("Button", parsed.Templates["MenuButton"].Root.Tag);

        var root = parsed.Root;
        Assert.Equal("root", root.Name);
        Assert.Equal(new[] { "screen", "dark" }, root.Classes);
        Assert.Equal("column", root.InlineStyle["flex-direction"]);
        Assert.Equal("12", root.InlineStyle["gap"]);
    }

    /// <summary>Inner text is captured; a real <c>text</c> attribute is a binding not a literal.</summary>
    [Fact]
    public void Label_TextAndBinding()
    {
        var parsedDocument = UiXmlParser.Parse(doc);
        var title = parsedDocument.Find("title")!;

        Assert.Equal("Fallback Title", title.Text);
        Assert.Equal("the title", title.Attributes["tooltip"]);
        var bind = Assert.Single(title.AttributeBindings);
        Assert.Equal("text", bind.TargetAttribute);
        Assert.Equal("App.Title", bind.Expression.Path);
    }

    /// <summary><c>&lt;Repeat&gt;</c> and <c>&lt;Instance&gt;</c> parse with their metadata.</summary>
    [Fact]
    public void RepeatAndInstance()
    {
        var parsedDocument = UiXmlParser.Parse(doc);

        var repeat = parsedDocument.Elements().Single(e => e.Repeat is not null);
        Assert.Equal("App.Levels", repeat.Repeat!.ItemsPath);
        Assert.Equal("level", repeat.Repeat.ItemAlias);

        var instance = Assert.Single(repeat.Children);
        Assert.Equal("MenuButton", instance.Instance!.TemplateName);
        Assert.Equal("{level.Name}", instance.Instance.Parameters["label"]);
    }

    /// <summary>Events, explicit and prefixed bindings, and custom-namespace elements.</summary>
    [Fact]
    public void EventsBindingsAndCustomNamespace()
    {
        var parsedDocument = UiXmlParser.Parse(doc);

        var play = parsedDocument.Find("play")!;
        Assert.Equal("OnPlay", play.Events["click"]);
        Assert.Contains(play.AttributeBindings, b => b.TargetAttribute == "enabled" && b.Expression.Path == "App.CanPlay");

        var gauge = parsedDocument.Find("fps")!;
        Assert.Equal("Gauge", gauge.Tag);
        Assert.Equal("https://example.com/x", gauge.Namespace);
        Assert.Contains(gauge.AttributeBindings, b => b.TargetAttribute == "value");

        Assert.Equal(2, parsedDocument.Bindings.Count);
        Assert.Contains(parsedDocument.Bindings, b => b is { Element: "play", Property: "enabled", Converter: "not" });
    }

    /// <summary>The document round-trips through JSON unchanged.</summary>
    [Fact]
    public void JsonRoundTrip()
    {
        var parsedDocument = UiXmlParser.Parse(doc);
        var back = UiDocument.FromJson(parsedDocument.ToJson());

        Assert.Equal(parsedDocument.ControllerType, back.ControllerType);
        Assert.Equal(parsedDocument.StyleSheets, back.StyleSheets);
        Assert.Equal(parsedDocument.Find("title")!.Text, back.Find("title")!.Text);
        Assert.Equal(parsedDocument.Elements().Count(), back.Elements().Count());
        Assert.Equal(parsedDocument.Bindings.Count, back.Bindings.Count);
    }

    /// <summary>A wrong root element is rejected with a location.</summary>
    [Fact]
    public void Parse_RejectsWrongRoot()
    {
        var ex = Assert.Throws<UiParseException>(() =>
            UiXmlParser.Parse("<Window xmlns=\"https://turian.mass4.org/ui\"><Label/></Window>"));

        Assert.Contains("Root element must be <UI>", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, ex.Line);
    }

    /// <summary>Malformed XML surfaces as a parse exception with a line number.</summary>
    [Fact]
    public void Parse_RejectsMalformedXml()
    {
        var ex = Assert.Throws<UiParseException>(() =>
            UiXmlParser.Parse("<UI xmlns=\"https://turian.mass4.org/ui\">\n  <Label>\n</UI>"));

        Assert.True(ex.Line >= 2);
    }

    /// <summary>A <c>&lt;Style&gt;</c> with no <c>src</c> is rejected at its location.</summary>
    [Fact]
    public void Parse_RejectsStyleWithoutSrc()
    {
        var ex = Assert.Throws<UiParseException>(() =>
            UiXmlParser.Parse("<UI xmlns=\"https://turian.mass4.org/ui\">\n  <Style/>\n  <Label/>\n</UI>"));

        Assert.Contains("requires a 'src'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, ex.Line);
    }
}
