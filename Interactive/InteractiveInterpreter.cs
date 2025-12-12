using Interactive.Utilities;
using Interactive.Attributes;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;

namespace Interactive;

public class InteractiveInterpreter : IDisposable
{
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _buffer.Clear();
        _functions.Clear();
        GC.SuppressFinalize(this);
    }

    private static ConsoleHelper Console => ConsoleHelper.Instance;

    private readonly Dictionary<string, (object, MethodInfo)> _functions = new(StringComparer.OrdinalIgnoreCase);
    private readonly StringBuilder _buffer = new();
    private bool HasBufferedText => _buffer.Length > 0;
    private volatile bool _disposed = false;

    public InteractiveInterpreter() => RegisterFunction(this);

    public async Task Start()
    {
        Console.WriteLine("交互控制台");
        Console.PrintLow("-- 输入 'help' 获取帮助信息 -- ");

        string? commandText;
        InteractiveCommand cmd;

        while (!_disposed)
        {
            try
            {
                commandText = Console.GetInputLine().TrimEnd();
                if (_disposed) break;

                if (string.IsNullOrWhiteSpace(commandText) && !HasBufferedText)
                    continue;

                if (commandText.EndsWith('\\'))
                {
                    WriteBuffer(commandText[..^1]);
                    continue;
                }
                else if (commandText.EndsWith("\\n"))
                {
                    WriteBufferLine(commandText[..^2]);
                    continue;
                }

                if (HasBufferedText) commandText = ConsumeBuffer() + commandText;
                cmd = InteractiveCommand.Parse(commandText);
                var ret = await InvokeFunction(cmd.PrimaryCommand, cmd.Args);
                if (ret is not null) Console.PrintLine(ret);
            }
            catch (FormatException ex)
            {
                Console.PrintError($"Invalid command line: {ex.Message}");
                continue;
            }
            catch (Exception ex)
            {
                Console.PrintError($"ERR! {ex.Message}");
                if (ex.InnerException is not null)
                {
                    Console.PrintError($"--> {ex.InnerException.Message}");
                }
                continue;
            }
        }
    }

    public Task RunOnNewThread()
    {
        var tcs = new TaskCompletionSource();
        var thread = new Thread((ThreadStart)delegate
        {
            Start().GetAwaiter().GetResult();
            tcs.SetResult();
        })
        {
            IsBackground = true,
            Name = "InteractiveInterpreterThread"
        };
        thread.Start();

        return tcs.Task;
    }

    private async Task<object?> InvokeFunction(string name, string[] parameters)
    {
        if (!_functions.TryGetValue(name, out var fnInfo))
            throw new InvalidOperationException($"Function '{name}' not found.");

        (object instance, MethodInfo fn) = fnInfo;
        object?[] convertedParams = [.. fn.GetParameters().Select((p, i) =>
        {
            if (i >= parameters.Length)
            {
                if (p.HasDefaultValue) return p.DefaultValue;
                throw new ArgumentException($"Missing parameter '{p.Name}' for function '{name}'.");
            }
            try
            {
                var converter = ParameterDeserializer.FromTypeOf(p.ParameterType)
                                ?? throw new ArgumentException($"No converter found for parameter type '{p.ParameterType.Name}'");
                return converter(parameters[i]);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Failed to convert parameter '{p.Name}' to type '{p.ParameterType.Name}': {ex.Message}");
            }
        })];

        object? ret;
        try
        {
            ret = fn.Invoke(instance, convertedParams);
        }
        catch (TargetInvocationException tex)
        {
            // 解包反射异常，让外层显示真实的错误信息
            throw tex.InnerException ?? tex;
        }
        if (ret is null) return null;

        if (fn.ReturnType == typeof(void))
        {
            return null;
        }
        else if (ret is string str)
        {
            return str;
        }
        else if (ret is Task<object?> task)
        {
            return await task;
        }
        else if (ret is ValueTask<object?> vtObj)
        {
            return await vtObj;
        }
        else if (ret is Task taskNoResult)
        {
            await taskNoResult;
            return null;
        }
        else
        {
            // This should never happen due to earlier checks,
            // or should throw an exception here ?
            return ret;
        }
    }

    public InteractiveInterpreter RegisterFunction<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(T instance) where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);

        var stdReturnType1 = typeof(Task<object?>);
        var stdReturnType3 = typeof(ValueTask<object?>);
        var stdReturnType2 = typeof(Task);
        var stdReturnType4 = typeof(string);

        bool IsValidReturnType(MethodInfo m) =>
            m.ReturnType == stdReturnType1 ||
            m.ReturnType == stdReturnType2 ||
            m.ReturnType == stdReturnType3 ||
            m.ReturnType == stdReturnType4 ||
            m.ReturnType == typeof(void);

        var methods = typeof(T)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<InteractiveFunctionAttribute>() is not null)
            .Where(IsValidReturnType);

        foreach (var method in methods)
        {
            var attr = method.GetCustomAttribute<InteractiveFunctionAttribute>();
            _functions[attr?.Name ?? method.Name] = (instance, method);
            if (attr?.Alias is string a)
                _functions[a] = (instance, method);
        }

        return this;
    }

    private void WriteBufferLine(string line)
    {
        _buffer.AppendLine(line);
    }

    private void WriteBuffer(string text)
    {
        _buffer.Append(text);
    }

    private string ConsumeBuffer()
    {
        var result = _buffer.ToString();
        _buffer.Clear();
        return result;
    }

    [InteractiveFunction(Description = "查看所有可用的命令")]
    public void Help()
    {
        Console.PrintLine("Available commands:");
        foreach (var fn in _functions)
        {
            var parameters = fn.Value.Item2.GetParameters();
            var parameterExpr = string.Join(" ", parameters.Select(p => p.IsOptional ? $"[{p.Name}]" : $"<{p.Name}>"));
            Console.Print($"- {fn.Key.ToLowerInvariant()} {parameterExpr}");

            var attr = fn.Value.Item2.GetCustomAttribute<InteractiveFunctionAttribute>();
            if (attr?.Description is not null)
            {
                Console.PrintLow($"  {attr.Description}");
            }
            else
            {
                Console.Print('\n');
            }

            if (parameters.Length > 0)
            {

                foreach (var p in parameters)
                {
                    var paramAttr = p.GetCustomAttribute<InteractiveParameterAttribute>();
                    Console.Print($"");

                    if (p.IsOptional)
                    {
                        Console.Print($"    · {p.Name}: {p.ParameterType.Name} [Optional");
                        if (p.HasDefaultValue)
                            Console.Print($", default = {p.DefaultValue ?? "null"}]");
                        else
                            Console.Print("]");
                    }
                    else
                    {
                        Console.Print($"    · {p.Name}: {p.ParameterType.Name}");
                    }

                    if (!string.IsNullOrWhiteSpace(paramAttr?.Description))
                    {
                        Console.PrintLow($" {paramAttr?.Description ?? ""}");
                    }
                    else
                    {
                        Console.Print('\n');
                    }
                }
            }

            Console.NextLine();
        }
    }
}