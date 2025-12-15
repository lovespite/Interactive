using Interactive.Attributes;
using Interactive.Utilities;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using System.Text;

namespace Interactive;

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
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
    private readonly Dictionary<string, string> _alias = new(StringComparer.OrdinalIgnoreCase);
    private readonly StringBuilder _buffer = new();
    private bool HasBufferedText => _buffer.Length > 0;
    private volatile bool _disposed = false;

    public bool AllowSearchSystemEnvironmentVariables { get; set; } = true;

    public InteractiveInterpreter() => RegisterFunction(this);

    public async Task Start()
    {
        Console.PrintLow("交互控制台");
        Console.PrintLow("-- 输入 'help' 获取帮助信息 -- ");

        string? commandText;
        List<InteractiveCommand> pipeline;

        while (!_disposed)
        {
            try
            {
                commandText = Console.GetInputLine().Trim();
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

                // 解析命令管线
                pipeline = InteractiveCommand.ParsePipeline(commandText);

                // 执行管线
                var ret = await ExecutePipeline(pipeline);

                // 输出
                PrintResult(ret);
            }
            catch (FormatException ex)
            {
                // 命令行格式错误
                Console.PrintError($"Bad command line format: {ex.Message}");
                continue;
            }
            catch (Exception ex)
            {
                var e = ex;
                Console.PrintError($"ERR! {e.Message}");
                int depth = 0;
                // 迭代打印内部异常
                while (e.InnerException is not null)
                {
                    Console.PrintError($"{new string(' ', depth * 2)}--> {e.InnerException.Message}");
                    e = e.InnerException;
                    ++depth;
                }

                continue;
            }
        }
    }

    private static void PrintResult(object? ret)
    {
        if (ret is null) return;

        if (ret is IEnumerable<object?> objects)
        {
            Console.PrintLine(objects.FormatPrint());
        }
        else if (ret is IEnumerable<KeyValuePair<string, object?>> kvPairs)
        {
            Console.PrintLine(kvPairs.FormatPrint());
        }
        else
        {
            Console.PrintLine(ret);
        }
    }

    private async Task<object?> ExecutePipeline(List<InteractiveCommand> pipeline)
    {
        object? pipeInput = null;
        for (int ptr = 0; ptr < pipeline.Count; ++ptr)
        {
            var cmd = pipeline[ptr];
            cmd.MapVariables(_contextlVariables, AllowSearchSystemEnvironmentVariables);

            var (Instance, Method) = GetMethodByName(cmd.PrimaryCommand);

            var parameters = MapParameters(Method.GetParameters(), cmd, pipeInput);

            var ret = Method.Invoke(Instance, parameters);

            pipeInput = await GetRealResult(ret);
        }
        return pipeInput;
    }

    private static async Task<object?> GetRealResult(object? ret)
    {
        if (ret is null) return null;

        if (ret is Task<object?> task)
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
            return ret;
        }
    }

    private object?[]? MapParameters(ParameterInfo[] pInfoArr, InteractiveCommand ic, object? pipeIn = null)
    {
        if (ic.IsMapped)
        {
            return MapParametersByName(pInfoArr, ic, pipeIn);
        }
        else
        {
            return MapParametersByIndex(pInfoArr, ic, pipeIn);
        }
    }

    private object?[]? MapParametersByName(ParameterInfo[] pInfoArr, InteractiveCommand ic, object? pipeIn = null)
    {
        static string AsVarName(int index) => $"$__default_parameter_{index}";

        Dictionary<string, object?> parameters = [];
        Dictionary<string, string> alias = [];

        foreach (var (index, pInfo) in pInfoArr.Index())
        {
            var pInfoAttr = pInfo.GetCustomAttribute<InteractiveParameterAttribute>();
            var parameterName = pInfo.Name ?? AsVarName(index);
            string? parameterAliasName = null;

            if (pInfoAttr?.Alias is string a)
            {
                alias[a] = parameterAliasName = parameterName;
            }

            if (pInfoAttr?.PipeIn == true && pipeIn is not null)
            {
                parameters[parameterName] = pipeIn;
                continue;
            }

            if (ic.TryGet(parameterName, out string? parameterValueRaw) ||
                (parameterAliasName is not null && ic.TryGet(parameterAliasName, out parameterValueRaw)))
            {
                var converter = ParameterDeserializer.FromTypeOf(pInfo.ParameterType)
                                ?? throw new ArgumentException($"No converter found for parameter type '{pInfo.ParameterType.Name}'");
                try
                {
                    parameters[parameterName] = parameterValueRaw is null ? null : converter(parameterValueRaw);
                }
                catch (Exception innerEx)
                {
                    throw new ArgumentException($"Failed to convert parameter '{parameterName}' to type '{pInfo.ParameterType.Name}'", innerEx);
                }
            }
            else
            {
                if (pInfo.IsOptional && pInfo.HasDefaultValue)
                {
                    parameters[parameterName] = pInfo.DefaultValue;
                    continue;
                }
                else
                {
                    throw new ArgumentException($"Missing parameter '{parameterName}' for function '{ic.PrimaryCommand}'.");
                }
            }
        }

        return [.. parameters.ToSortedList(pInfoArr.Select((p, i) => p?.Name ?? AsVarName(i)))];
    }

    private object?[]? MapParametersByIndex(ParameterInfo[] pInfoArr, InteractiveCommand ic, object? pipeIn = null)
    {
        object?[] parameters = new object?[pInfoArr.Length];
        int icPtr = 0;

        for (int i = 0; i < parameters.Length; ++i)
        {
            var pInfo = pInfoArr[i];
            var isPipeIn = pInfo.GetCustomAttribute<InteractiveParameterAttribute>()?.PipeIn == true;

            if (isPipeIn && pipeIn is not null)
            {
                //if (pInfo.ParameterType != pipeIn.GetType())
                //{
                //    throw new ArgumentException($"Cannot pipe input of type '{pipeIn.GetType().Name}' to parameter '{pInfo.Name}' of type '{pInfo.ParameterType.Name}'");
                //}
                parameters[i] = pipeIn;
                continue;
            }

            if (!ic.TryGet(icPtr++, out var paramExpr))
            {
                if (pInfo.IsOptional && pInfo.HasDefaultValue)
                {
                    parameters[i] = pInfo.DefaultValue;
                    continue;
                }
                else
                {
                    throw new ArgumentException($"Missing parameter '{pInfo.Name}' for function '{ic.PrimaryCommand}'.");
                }
            }

            var converter = ParameterDeserializer.FromTypeOf(pInfo.ParameterType)
                            ?? throw new ArgumentException($"No converter found for parameter type '{pInfo.ParameterType.Name}'");

            try
            {
                parameters[i] = paramExpr is null ? null : converter(paramExpr);
            }
            catch (Exception innerEx)
            {
                throw new ArgumentException($"Failed to convert parameter '{pInfo.Name}' to type '{pInfo.ParameterType.Name}'", innerEx);
            }
        }

        return parameters;
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

    private static readonly List<Type> s_SupportedReturnTypes = [
        typeof(void), // 无返回值 
        typeof(object), typeof(string), typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal), // 基本类型
        typeof(object[]), typeof(string[]), typeof(int[]), typeof(long[]), typeof(float[]), typeof(double[]), typeof(decimal[]), // 基本类型数组
        typeof(Task), typeof(Task<object?>), typeof(ValueTask<object?>), // 可等待类型
    ];

    public static bool IsSupportedReturnType(Type returnType)
    {
        return s_SupportedReturnTypes.Contains(returnType);
    }

    public static bool IsSupportedReturnType(MethodInfo method)
    {
        return IsSupportedReturnType(method.ReturnType);
    }

    public InteractiveInterpreter RegisterFunction<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(T instance) where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);

        var methods = typeof(T)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<InteractiveFunctionAttribute>() is not null)
            .Where(IsSupportedReturnType);

        foreach (var method in methods)
        {
            var attr = method.GetCustomAttribute<InteractiveFunctionAttribute>();
            var fnName = attr?.Name ?? method.Name;
            _functions[fnName] = (instance, method);
            if (attr?.Alias is string a) _alias[a] = fnName;
        }

        return this;
    }

    private bool HasMethod(string name)
    {
        return _functions.ContainsKey(name) || (_alias.ContainsKey(name) && _functions.ContainsKey(_alias[name]));
    }

    private (object Instance, MethodInfo Method) GetMethodByName(string name)
    {
        if (_functions.TryGetValue(name, out var fnInfo))
        {
            return fnInfo;
        }
        else if (_alias.TryGetValue(name, out var actualName) && _functions.TryGetValue(actualName, out fnInfo))
        {
            return fnInfo;
        }
        else
        {
            throw new InvalidOperationException($"Function '{name}' not found.");
        }
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

    #region Built-in Functions

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
                            Console.Print($", default = {paramAttr?.DefaultValueDisplayText ?? p.DefaultValue ?? "null"}]");
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

    [InteractiveFunction(Name = "man", Description = "查看指定命令的文档")]
    public string Manual([InteractiveParameter(Description = "要查询的命令名称或别名")] string command)
    {
        var (_, Method) = GetMethodByName(command);
        var attr = Method.GetCustomAttribute<InteractiveFunctionAttribute>();
        var parameters = Method.GetParameters();
        var sb = new StringBuilder();

        sb.AppendLine($" Command:     {command}");
        sb.AppendLine($" Alias:       {_alias.FirstOrDefault(kv => kv.Value.Equals(command, StringComparison.OrdinalIgnoreCase)).Key ?? "N/A"}");
        sb.AppendLine($" Description: {attr?.Description}");

        sb.Append($" Return Type: {Method.ReturnType.Name}");
        if (Method.ReturnType.IsGenericType)
        {
            sb.AppendLine($"<{string.Join(", ", Method.ReturnType.GetGenericArguments().Select(t => t.Name))}>");
        }
        else
        {
            sb.AppendLine();
        }

        if (parameters.Length > 0)
        {
            sb.AppendLine($" Parameters:");
            foreach (var p in parameters)
            {
                var paramAttr = p.GetCustomAttribute<InteractiveParameterAttribute>();
                sb.Append($"  - {p.Name}: {p.ParameterType.Name}");
                if (paramAttr?.PipeIn == true)
                {
                    sb.Append(" [PipeIn]");
                }
                if (p.IsOptional)
                {
                    sb.Append(" [Optional");
                    if (p.HasDefaultValue)
                        sb.Append($", default = {paramAttr?.DefaultValueDisplayText ?? p.DefaultValue ?? "null"}");
                    sb.Append(']');
                }
                // sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(paramAttr?.Description))
                {
                    sb.AppendLine($"  {paramAttr.Description}");
                }
                else
                {
                    sb.AppendLine();
                }
            }
        }
        return sb.ToString();
    }


    [InteractiveFunction(Alias = "grep", Description = "在输入的字符串中查找指定子字符串")]
    public string FindStr(
        [InteractiveParameter(PipeIn = true)] string input,
        [InteractiveParameter(Description = "要查找的字符串")] string find,
        [InteractiveParameter(Description = "换行符", DefaultValueDisplayText = "\\n")] string? newLine = "\n")
    {
        var lines = input.Split(newLine, StringSplitOptions.None);
        var matchedLines = lines.Where(line => line.Contains(find, StringComparison.OrdinalIgnoreCase));
        return string.Join(newLine, matchedLines.Select(x => x.Trim()));
    }

    [InteractiveFunction(Name = "printc", Description = "格式化输出集合")]
    public string PrintCollection(
        [InteractiveParameter(Description = "集合，支持键值对集合、对象集合", PipeIn = true)] object input,
        [InteractiveParameter(Description = "分隔符", DefaultValueDisplayText = "\\n")] string? separator = "\n",
        [InteractiveParameter(Description = "忽略空项目")] bool ignoreNull = true)
    {
        var sb = new StringBuilder();

        if (input is IEnumerable<KeyValuePair<string, object?>> kvPairs)
        {
            foreach (var kv in kvPairs)
            {
                sb.Append(kv.Key)
                    .Append(" = ")
                    .AppendLine(Convert.ToString(kv.Value));
            }
        }
        else if (input is IEnumerable<object?> enumerable)
        {
            foreach (var o in enumerable)
            {
                if (ignoreNull && o is null) continue;
                sb.Append(Convert.ToString(o)).Append(separator);
            }
        }
        else if (input is string textArr)
        {
            sb.Append(textArr);
        }

        return sb.ToString().TrimEnd();
    }

    [InteractiveFunction(Name = "println", Description = "格式化输出对象集合")]
    public string PrintCollection(
    [InteractiveParameter(Description = "对象集合", PipeIn = true)] IEnumerable<object?> input,
    [InteractiveParameter(Description = "分隔符", DefaultValueDisplayText = "\\n")] string? separator = "\n",
    [InteractiveParameter(Description = "忽略空项目")] bool ignoreNull = true)
    {
        var sb = new StringBuilder();

        foreach (var item in input)
        {
            if (ignoreNull && item is null) continue;
            sb.Append(Convert.ToString(item)).Append(separator);
        }

        return sb.ToString().TrimEnd();
    }

    [InteractiveFunction(Description = "以特定分隔符切割字符串")]
    public string[] Split(
        [InteractiveParameter(Description = "输入字符串", PipeIn = true)] string input,
        [InteractiveParameter(Description = "分隔符")] string separetor,
        [InteractiveParameter(Description = "最大分割数量")] int maxCount = int.MaxValue,
        [InteractiveParameter(Description = "是否移除空项")] bool removeEmpty = true,
        [InteractiveParameter(Description = "是否移除前后空字符")] bool trimEntry = true)
    {
        StringSplitOptions options = StringSplitOptions.None;

        if (removeEmpty) options |= StringSplitOptions.RemoveEmptyEntries;
        if (trimEntry) options |= StringSplitOptions.TrimEntries;

        return input.Split(separetor, maxCount, options);
    }

    #endregion

    #region Context Self Management

    [InteractiveFunction(Description = "启用指定属性", Name = "prop-enable")]
    public string EnableProperty([InteractiveParameter(Description = "属性名称", PipeIn = true)] string propName)
    {
        var prop = GetProperty(propName)
                   ?? throw new ArgumentException($"Property '{propName}' not found.", nameof(propName));

        if (prop.PropertyType != typeof(bool))
        {
            throw new ArgumentException($"Property '{propName}' is not of type bool.", nameof(propName));
        }

        prop.SetValue(this, true);
        return $"Property '{propName}' enabled.";
    }

    [InteractiveFunction(Description = "禁用指定属性", Name = "prop-disable")]
    public string DisableProperty([InteractiveParameter(Description = "属性名称", PipeIn = true)] string propName)
    {
        var prop = GetProperty(propName)
                   ?? throw new ArgumentException($"Property '{propName}' not found.", nameof(propName));
        if (prop.PropertyType != typeof(bool))
        {
            throw new ArgumentException($"Property '{propName}' is not of type bool.", nameof(propName));
        }
        prop.SetValue(this, false);

        return $"Property '{propName}' disabled.";
    }

    [InteractiveFunction(Description = "查看属性当前的值", Name = "prop-get")]
    public object? GetPropertyValue([InteractiveParameter(Description = "属性名称", PipeIn = true)] string propName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propName, nameof(propName));

        var prop = GetProperty(propName)
                   ?? throw new ArgumentException($"Property '{propName}' not found.", nameof(propName));
        var value = prop.GetValue(this);

        return value;
    }

    [InteractiveFunction(Description = "设置属性的值", Name = "prop-set")]
    public string SetPropertyValue(
        [InteractiveParameter(Description = "属性名称")] string propName,
        [InteractiveParameter(Description = "属性值", PipeIn = true)] string valueExpr
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propName, nameof(propName));
        var prop = GetProperty(propName)
                   ?? throw new ArgumentException($"Property '{propName}' not found.", nameof(propName));
        var converter = ParameterDeserializer.FromTypeOf(prop.PropertyType)
                        ?? throw new ArgumentException($"No converter found for property type '{prop.PropertyType.Name}'");
        try
        {
            var value = converter(valueExpr);
            prop.SetValue(this, value);
            return $"Property '{prop.Name}' set to '{Convert.ToString(value)}'.";
        }
        catch (Exception innerEx)
        {
            throw new ArgumentException($"Failed to convert value '{valueExpr}' to type '{prop.PropertyType.Name}'", innerEx);
        }
    }

    [InteractiveFunction(Description = "列出所有属性及其当前值", Name = "prop-list")]
    public object ListProperties(string? propNamePattern = null, bool withValue = false)
    {
        if (withValue)
        {
            var list = new List<KeyValuePair<string, object?>>();
            IEnumerable<PropertyInfo> props = GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

            if (propNamePattern is not null) props = props.Where(x => x.Name.Contains(propNamePattern, StringComparison.OrdinalIgnoreCase));

            foreach (var prop in props)
            {
                var value = prop.GetValue(this);
                list.Add(new KeyValuePair<string, object?>(prop.Name, value));
            }
            return list;
        }
        else
        {
            return GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);
        }
    }

    private PropertyInfo? GetProperty(string propName)
    {
        return this.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
    }

    #endregion

    #region Variable Management

    private readonly Dictionary<string, string> _contextlVariables = [];

    [InteractiveFunction(Name = "set", Description = "设置变量的值")]
    public string SetVariable(
        [InteractiveParameter(Description = "变量名称，至少1个字符，只能包含字母、数字、下划线")] string name,
        [InteractiveParameter(Description = "变量值", PipeIn = true)] string value
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        ArgumentNullException.ThrowIfNull(value);

        name = name.Trim();

        if (!name.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            throw new ArgumentException("Variable name can only contain letters, digits and underscores.", nameof(name));
        }

        _contextlVariables[name] = value;
        return value;
    }

    [InteractiveFunction(Name = "get", Description = "获取变量的值")]
    public string GetVariable(
        [InteractiveParameter(Description = "变量名称")] string name
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));

        if (_contextlVariables.TryGetValue(name, out var value))
        {
            return value;
        }
        else
        {
            throw new KeyNotFoundException($"Variable '{name}' not found.");
        }
    }

    [InteractiveFunction(Name = "del", Description = "删除变量")]
    public void DeleteVariable(
        [InteractiveParameter(Description = "变量名称")] string name
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        if (name.StartsWith("$(") && name.EndsWith(')'))
        {
            name = name[2..^1];
        }
        if (!_contextlVariables.Remove(name))
        {
            throw new KeyNotFoundException($"Variable '{name}' not found.");
        }
    }

    #endregion

    #region System Helpers

    [InteractiveFunction(Description = "清除控制台屏幕内容", Name = "cls", Alias = "reset")]
    public void ClearConsole() => Console.Clear();

    [InteractiveFunction(Description = "获取系统环境变量, 默认检测顺序: 进程>用户>本机", Name = "getx")]
    public string? GetEnvironmentVariable(
        [InteractiveParameter(Description = "环境变量名称")] string name,
        [InteractiveParameter(Description = "作用域: 0=进程/1=用户/2=本机(需要管理员权限)/3=自动")] int target = 3

        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));

        if (target >= 3 || target < 0)
        {
            return Environment.GetEnvironmentVariable(name)
                // 需要依次检测，虽然系统会为每个进程加载用户和机器的环境变量，但是用户修改了变量后，进程中的值不会更新
                ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
        }

        return Environment.GetEnvironmentVariable(name, (EnvironmentVariableTarget)target);
    }

    [InteractiveFunction(Description = "设置系统环境变量, 作用域: 0=进程/1=用户/2=本机(需要管理员权限)", Name = "setx")]
    public void SetEnvironmentVariable(
        [InteractiveParameter(Description = "环境变量名称")] string name,
        [InteractiveParameter(Description = "环境变量值", PipeIn = true)] string value,
        [InteractiveParameter(Description = "作用域: 0=进程/1=用户/2=本机(需要管理员权限)", DefaultValueDisplayText = "0")] int target = 0
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        EnvironmentVariableTarget envTarget = target switch
        {
            0 => EnvironmentVariableTarget.Process,
            1 => EnvironmentVariableTarget.User,
            2 => EnvironmentVariableTarget.Machine,
            _ => throw new ArgumentOutOfRangeException(nameof(target), "Target must be 0, 1 or 2.")
        };
        if (envTarget == EnvironmentVariableTarget.Machine)
        {
            // Check for admin rights
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException("Setting machine-level environment variables is only supported on Windows.");
            }
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                throw new UnauthorizedAccessException("Setting machine-level environment variables requires administrator privileges.");
            }
        }
        Environment.SetEnvironmentVariable(name, value, envTarget);
    }

    [InteractiveFunction(Description = "删除系统环境变量, 作用域: 0=进程/1=用户/2=本机(需要管理员权限)", Name = "delx")]
    public void DeleteEnvironmentVariable(
        [InteractiveParameter(Description = "环境变量名称")] string name,
        [InteractiveParameter(Description = "作用域: 0=进程/1=用户/2=本机(需要管理员权限)", DefaultValueDisplayText = "0")] int target = 0
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        EnvironmentVariableTarget envTarget = target switch
        {
            0 => EnvironmentVariableTarget.Process,
            1 => EnvironmentVariableTarget.User,
            2 => EnvironmentVariableTarget.Machine,
            _ => throw new ArgumentOutOfRangeException(nameof(target), "Target must be 0, 1 or 2.")
        };
        if (envTarget == EnvironmentVariableTarget.Machine)
        {
            // Check for admin rights
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException("Deleting machine-level environment variables is only supported on Windows.");
            }
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                throw new UnauthorizedAccessException("Deleting machine-level environment variables requires administrator privileges.");
            }
        }
        Environment.SetEnvironmentVariable(name, null, envTarget);
    }

    #endregion
}

internal static class DictionaryExtensions
{
    public static string FormatPrint(this IEnumerable<KeyValuePair<string, object?>> collection)
    {
        var sb = new StringBuilder();
        foreach (var kv in collection)
        {
            sb.AppendLine($"[{kv.Key} = {Convert.ToString(kv.Value)}]");
        }
        return sb.ToString();
    }

    public static string FormatPrint(this IEnumerable<object?> collection, int maxColmun = 4)
    {
        var sb = new StringBuilder();
        int count = 0;
        foreach (var item in collection)
        {
            sb.Append('[').Append(item).Append(']');
            count++;
            if (count % maxColmun == 0)
            {
                sb.AppendLine();
            }
            else
            {
                sb.Append('\t');
            }
        }
        return sb.ToString();
    }

    public static List<object?> ToSortedList(this Dictionary<string, object?> dictionary, IEnumerable<string> keyOrder)
    {
        var list = new List<object?>();
        foreach (var key in keyOrder)
        {
            if (dictionary.TryGetValue(key, out var value))
            {
                list.Add(value);
            }
            else
            {
                list.Add(null);
            }
        }
        return list;
    }
}