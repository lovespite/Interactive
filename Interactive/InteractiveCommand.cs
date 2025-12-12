namespace Interactive;

public class InteractiveCommand(string primaryCommand, string[] args)
{
    public string PrimaryCommand { get; } = primaryCommand;

    public string[] Args { get; } = args;

    const char Quote1 = '"';
    const char Quote2 = '\'';
    const char Quote3 = '`';
    const char EscapeChar = '\\';

    public static InteractiveCommand Parse(string input)
    {
        var index = input.IndexOf(' ');
        var primaryCommand = index == -1 ? input : input[..index];

        var argsPart = index == -1 ? string.Empty : input[(index + 1)..];

        ushort currentCharIndex = 0;
        char quoteChar = '\0';

        var args = new List<string>();
        var sb = new System.Text.StringBuilder();

        while (currentCharIndex < argsPart.Length)
        {
            char c = argsPart[currentCharIndex];
            currentCharIndex++;

            if (char.IsWhiteSpace(c))
            {
                if (quoteChar == '\0') // 未在引号内
                {
                    if (sb.Length == 0) continue;
                    args.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
                continue;
            }

            if (c == EscapeChar)
            {
                if (quoteChar == '\0')
                {
                    // 不在引号内，转义字符作为普通字符处理
                    sb.Append(c);
                    continue;
                }

                currentCharIndex++;
                if (currentCharIndex >= argsPart.Length) throw new FormatException("无效的转义字符位置");
                c = argsPart[currentCharIndex - 1] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '"' => '"',
                    '\'' => '\'',
                    '`' => '`',
                    '0' => '\0',
                    'u' => ReadEscapedUnicode(argsPart, ref currentCharIndex),
                    _ => argsPart[currentCharIndex - 1],
                };
                sb.Append(c);
                continue;
            }

            if (c == Quote1 || c == Quote2 || c == Quote3)
            {
                if (quoteChar == '\0')
                {
                    if (sb.Length > 0) throw new FormatException("引号前不应有其他字符");
                    quoteChar = c; // 开始引号
                }
                else if (quoteChar == c)
                {
                    quoteChar = '\0'; // 结束引号
                    args.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c); // 引号内的其他引号作为普通字符处理
                }
                continue;
            }

            sb.Append(c);
        }

        if (quoteChar != '\0')
            throw new FormatException("引号未闭合");

        if (sb.Length > 0)
        {
            args.Add(sb.ToString());
        }

        return new InteractiveCommand(primaryCommand, [.. args]);
    }

    private static char ReadEscapedUnicode(string argsPart, ref ushort currentCharIndex)
    {
        if (currentCharIndex + 4 > argsPart.Length)
            throw new FormatException("无效的 Unicode 转义字符位置");
        var hex = argsPart.Substring(currentCharIndex, 4);
        if (!ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var code))
            throw new FormatException("无效的 Unicode 转义字符位置");
        currentCharIndex += 4;
        return (char)code;
    }

    internal bool TryGet(int index, out string value)
    {
        if (index < 0 || index >= Args.Length)
        {
            value = string.Empty;
            return false;
        }
        value = Args[index];
        return true;
    }
}
