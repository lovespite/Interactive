using Interactive.Utilities;
using System.Diagnostics.SymbolStore;

namespace Interactive;

public class InteractiveCommand
{
    public string PrimaryCommand { get; private set; }

    public string[] Args { get; private set; }

    const char Quote1 = '"';
    const char Quote2 = '\'';
    const char Quote3 = '`';
    const char EscapeChar = '\\';

    private readonly Dictionary<string, string> _namedArgs = [];

    public InteractiveCommand(string primaryCommand, string[] args)
    {
        PrimaryCommand = primaryCommand;
        Args = args;
        MapArgs();
    }

    public void MapVariables(IDictionary<string, string> variables, bool allowSearchSystemEnvironment)
    {
        VariableHelper.ReplaceOptions options = VariableHelper.ReplaceOptions.RemoveIfNotFound;
        if (allowSearchSystemEnvironment)
        {
            options |= VariableHelper.ReplaceOptions.SearchSystemEnvironment;
        }

        PrimaryCommand = variables.MapVariables(PrimaryCommand, options);
        if (Args.Length == 0) return;
        if (_namedArgs.Count > 0)
        {
            foreach (var key in _namedArgs.Keys.ToArray())
            {
                _namedArgs[key] = variables.MapVariables(_namedArgs[key], options);
            }
        }
        else
        {
            for (int i = 0; i < Args.Length; i++)
            {
                Args[i] = variables.MapVariables(Args[i], options);
            }
        }
    }

    private void MapArgs()
    {
        string? name = null;
        for (int i = 0; i < Args.Length; i++)
        {
            var arg = Args[i];
            if (arg.StartsWith('-'))
            {
                if (name is not null)
                {
                    // 上一个参数是标记，没有值
                    _namedArgs[name] = "true"; // 转换为开关
                }
                name = arg.TrimStart('-');
                continue;
            }

            if (name is not null)
            {
                _namedArgs[name] = arg;
                name = null;
            }
        }

        if (name is not null)
        {
            // 上一个参数是标记，没有值
            _namedArgs[name] = "true"; // 转换为开关
        }
    }

    public static List<InteractiveCommand> ParsePipeline(string input)
    {
        var commands = new List<InteractiveCommand>();
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

            if (c == '|')
            {
                if (quoteChar != '\0')
                {
                    sb.Append(c); // 引号内的管道符作为普通字符处理
                    continue;
                }
                // 管道符，结束当前命令
                if (sb.Length > 0)
                {
                    args.Add(sb.ToString());
                    sb.Clear();
                }
                commands.Add(new InteractiveCommand(primaryCommand, [.. args]));
                args.Clear();
                // 读取下一个命令的主命令
                while (currentCharIndex < argsPart.Length && char.IsWhiteSpace(argsPart[currentCharIndex]))
                {
                    currentCharIndex++;
                }

                var nextCommandStart = currentCharIndex;

                while (currentCharIndex < argsPart.Length && !char.IsWhiteSpace(argsPart[currentCharIndex]) && argsPart[currentCharIndex] != '|')
                {
                    currentCharIndex++;
                }

                if (nextCommandStart == currentCharIndex)
                    throw new FormatException("无效的命令格式");

                primaryCommand = argsPart[nextCommandStart..currentCharIndex];
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

        commands.Add(new InteractiveCommand(primaryCommand, [.. args]));

        return commands;
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

    public bool TryGet(int index, out string? value)
    {
        if (index < 0 || index >= Args.Length)
        {
            value = null;
            return false;
        }
        value = Args[index];
        return true;
    }

    public bool TryGet(string name, out string? value)
    {
        return _namedArgs.TryGetValue(name, out value);
    }

    public bool IsMapped => _namedArgs.Count > 0;

    public bool HasFlag(string name)
    {
        return _namedArgs.ContainsKey(name);
    }
}
