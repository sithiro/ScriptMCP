public static class FormatHelper
{
    public static string Banner(string text)
    {
        var border = new string('=', text.Length + 4);
        return $"{border}\n| {text} |\n{border}";
    }
}
