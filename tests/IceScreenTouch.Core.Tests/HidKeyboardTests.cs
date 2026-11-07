using IceScreenTouch.Core;

namespace IceScreenTouch.Core.Tests;

public class HidKeyboardTests
{
    private const byte Shift = HidModifiers.LeftShift;

    [Theory]
    [InlineData('a', 0x04, 0)]
    [InlineData('z', 0x1D, 0)]
    [InlineData('A', 0x04, Shift)]
    [InlineData('1', 0x1E, 0)]
    [InlineData('0', 0x27, 0)]
    [InlineData(' ', 0x2C, 0)]
    [InlineData('\n', 0x28, 0)]
    [InlineData('\t', 0x2B, 0)]
    public void LettersDigitsAndWhitespaceAreTheSameOnBothLayouts(char c, byte usage, byte modifiers)
    {
        foreach (var layout in Enum.GetValues<HidKeyboardLayout>())
        {
            Assert.True(HidKeyboard.TryMap(c, layout, out var key));
            Assert.Equal(new HidKey(usage, modifiers), key);
        }
    }

    [Theory]
    [InlineData('@', 0x1F, Shift, 0x2F, 0)]
    [InlineData('"', 0x34, Shift, 0x1F, Shift)]
    [InlineData(':', 0x33, Shift, 0x34, 0)]
    [InlineData('=', 0x2E, 0, 0x2D, Shift)]
    [InlineData('_', 0x2D, Shift, 0x87, Shift)]
    [InlineData('\\', 0x31, 0, 0x87, 0)]
    [InlineData('|', 0x31, Shift, 0x89, Shift)]
    [InlineData(']', 0x30, 0, 0x32, 0)]
    [InlineData('(', 0x26, Shift, 0x25, Shift)]
    public void SymbolsFollowTheLayout(char c, byte usUsage, byte usModifiers, byte jisUsage, byte jisModifiers)
    {
        Assert.True(HidKeyboard.TryMap(c, HidKeyboardLayout.Us, out var us));
        Assert.Equal(new HidKey(usUsage, usModifiers), us);
        Assert.True(HidKeyboard.TryMap(c, HidKeyboardLayout.Jis, out var jis));
        Assert.Equal(new HidKey(jisUsage, jisModifiers), jis);
    }

    [Fact]
    public void EveryPrintableAsciiCharacterHasAKeyOnBothLayouts()
    {
        foreach (var layout in Enum.GetValues<HidKeyboardLayout>())
        {
            for (var c = ' '; c <= '~'; c++)
            {
                Assert.True(HidKeyboard.TryMap(c, layout, out _), $"{layout}: '{c}'");
            }
        }
    }

    [Fact]
    public void OnlyJisHasTheYenSignAndNeitherHasJapanese()
    {
        Assert.True(HidKeyboard.TryMap('¥', HidKeyboardLayout.Jis, out _));
        Assert.False(HidKeyboard.TryMap('¥', HidKeyboardLayout.Us, out _));
        Assert.False(HidKeyboard.TryMap('あ', HidKeyboardLayout.Jis, out _));
        Assert.False(HidKeyboard.TryMap('\r', HidKeyboardLayout.Us, out _));
    }

    [Theory]
    [InlineData("enter", 0x28, 0)]
    [InlineData("Left", 0x50, 0)]
    [InlineData("f12", 0x45, 0)]
    [InlineData("kana", 0x90, 0)]
    [InlineData("eisu", 0x91, 0)]
    [InlineData("0x90", 0x90, 0)]
    [InlineData("ctrl+space", 0x2C, HidModifiers.LeftControl)]
    [InlineData("cmd+a", 0x04, HidModifiers.LeftGui)]
    [InlineData("cmd+shift+z", 0x1D, HidModifiers.LeftGui | HidModifiers.LeftShift)]
    [InlineData("A", 0x04, Shift)]
    [InlineData("cmd++", 0x33, HidModifiers.LeftGui | Shift)] // '+' on JIS is Shift+;
    public void KeyNamesParse(string text, byte usage, byte modifiers)
    {
        Assert.True(HidKeyboard.TryParse(text, HidKeyboardLayout.Jis, out var key));
        Assert.Equal(new HidKey(usage, modifiers), key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hyper+a")]
    [InlineData("nosuchkey")]
    [InlineData("0xZZ")]
    [InlineData("ctrl+")]
    public void BadKeyNamesAreRejected(string text) =>
        Assert.False(HidKeyboard.TryParse(text, HidKeyboardLayout.Us, out _));
}
