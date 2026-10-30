using CxShell.Controls;
using CxShell.Terminal;

namespace CxShell.Tests;

public sealed class TerminalControlSelectionTests
{
    [Fact]
    public void BuildSelectedText_JoinsAutomaticallyWrappedRows()
    {
        var buffer = CreateBuffer("abcdefghijkl");

        var selected = TerminalControl.BuildSelectedText(buffer, 0, 0, 2, 2);

        Assert.Equal("abcdefghijkl", selected);
    }

    [Fact]
    public void BuildSelectedText_PreservesSpacesAtSoftWrapBoundary()
    {
        var buffer = CreateBuffer("ab   X");

        var selected = TerminalControl.BuildSelectedText(buffer, 0, 0, 1, 1);

        Assert.Equal("ab   X", selected);
    }

    [Fact]
    public void BuildSelectedText_PreservesWrittenSpaceBeforeWideCharacterWrap()
    {
        var buffer = CreateBuffer("abcd 中");

        var selected = TerminalControl.BuildSelectedText(buffer, 0, 0, 1, 3);

        Assert.Equal("abcd 中", selected);
    }

    [Fact]
    public void BuildSelectedText_OmitsWideCharacterWrapGap()
    {
        var buffer = CreateBuffer("abcd中X");

        var selected = TerminalControl.BuildSelectedText(buffer, 0, 0, 1, 3);

        Assert.Equal("abcd中X", selected);
    }

    [Fact]
    public void BuildSelectedText_PreservesExplicitLineBreaks()
    {
        var buffer = CreateBuffer("abcde\r\nx");

        var selected = TerminalControl.BuildSelectedText(buffer, 0, 0, 1, 1);

        Assert.Equal($"abcde{Environment.NewLine}x", selected);
    }

    [Fact]
    public void BuildSelectedText_JoinsWrappedRowsInScrolledBackViewport()
    {
        var buffer = CreateBuffer("abcdefghijklmnop");

        var selected = TerminalControl.BuildSelectedText(buffer, 0, 0, 2, 1);

        Assert.Equal("abcdefghijk", selected);
    }

    [Fact]
    public void ContentRowsKeepPointingAtTheSameTextWhenTheViewportScrolls()
    {
        var buffer = CreateBuffer("abcdefghijklmnop");
        var contentRow = buffer.ScrollbackBase + buffer.ScrollbackCount;

        var bottomCell = buffer.GetContentCell(contentRow, 0);
        var scrolledCell = buffer.GetViewportCell(1, 0, scrollOffset: 1);

        Assert.Equal("f", bottomCell.GetText());
        Assert.Equal(bottomCell.GetText(), scrolledCell.GetText());
        Assert.Equal("fghij", TerminalControl.BuildSelectedText(buffer, contentRow, 0, contentRow, 5));
    }

    private static TerminalBuffer CreateBuffer(string text)
    {
        var buffer = new TerminalBuffer(columns: 5, rows: 3, maxScrollback: 20);
        new AnsiParser(buffer).Process(text);
        return buffer;
    }
}
