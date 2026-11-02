using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;

namespace LogiBoardToolBox.Core.Macros;

/// <summary>Bounded partial evaluation of deterministic Lua syntax. Host state and RNG are never sampled.</summary>
public static class LuaMacroImport
{
    private const int MaximumTableEntries = 65536;
    public static MacroProgram ConvertToMacro(string source, int triggerButton = 1, MacroRepeat repeat = MacroRepeat.Once) => Convert(source, triggerButton, repeat).CompileProgram();
    public static string ConvertToScript(string source, int triggerButton = 1, MacroRepeat repeat = MacroRepeat.Once) => Convert(source, triggerButton, repeat).Script;
    public static LuaOnboardConversion Convert(string source, int triggerButton = 1, MacroRepeat repeat = MacroRepeat.Once, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(source)) throw new FormatException("Lua 脚本不能为空。");
        LuaSourceFile.ValidateSource(source);
        if (triggerButton is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(triggerButton));
        if (!Enum.IsDefined(repeat)) throw new ArgumentOutOfRangeException(nameof(repeat));
        if (LuaMacroExport.TryRestore(source, triggerButton, repeat, out var restored)) return restored;
        var statements = new Parser(source, cancellationToken).Parse();
        try { _ = new Script(CoreModules.Preset_HardSandbox).LoadString(source, codeFriendlyName: "onboard-import.lua"); }
        catch (SyntaxErrorException error) { throw new FormatException("Lua 语法无效：" + error.DecoratedMessage, error); }
        cancellationToken.ThrowIfCancellationRequested();
        return new Compiler(statements, triggerButton, repeat, cancellationToken).Run();
    }
    private static NotSupportedException Unsupported(string detail) => new("Lua 板载转换：" + detail + "。原始 Lua 可在软件层运行；未写入鼠标。");
    private abstract record Expr;
    private sealed record Literal(object? Value) : Expr;
    private sealed record Name(string Value) : Expr;
    private sealed record Grouped(Expr Value) : Expr;
    private sealed record Index(Expr Target, Expr Key) : Expr;
    private sealed record Unary(string Op, Expr Value) : Expr;
    private sealed record Binary(string Op, Expr Left, Expr Right) : Expr;
    private sealed record Call(Expr Target, List<Expr> Arguments) : Expr;
    private sealed record TableExpr(List<(Expr? Key, Expr Value)> Fields) : Expr;
    private sealed record FunctionExpr(List<string> Parameters, List<Stmt> Body) : Expr;
    private abstract record Stmt;
    private sealed record Assign(List<Expr> Targets, List<Expr> Values, bool Local) : Stmt;
    private sealed record Invoke(Call Call) : Stmt;
    private sealed record Def(Expr Target, FunctionExpr Function, bool Local) : Stmt;
    private sealed record If(List<(Expr Condition, List<Stmt> Body)> Arms, List<Stmt> Else) : Stmt;
    private sealed record For(string Name, Expr Start, Expr End, Expr Step, List<Stmt> Body) : Stmt;
    private sealed record Each(List<string> Names, List<Expr> Iterators, List<Stmt> Body) : Stmt;
    private sealed record While(Expr Condition, List<Stmt> Body, bool Until) : Stmt;
    private sealed record Block(List<Stmt> Body) : Stmt;
    private sealed record Return(List<Expr> Values) : Stmt;
    private sealed record Break : Stmt;
    private sealed class ReturnFlow(object?[] values) : Exception { public object?[] Values { get; } = values; }
    private sealed class BreakFlow : Exception;
    private sealed class Cell(object? value) { public object? Value = value; public bool Persistent; }
    private sealed class Scope
    {
        private readonly Scope? parent;
        public Scope Environment { get; }
        public Scope(Scope? parent = null) { this.parent = parent; Environment = parent?.Environment ?? this; }
        public Dictionary<string, Cell> Cells { get; } = new(StringComparer.Ordinal);
        public Cell? Find(string name) => Cells.GetValueOrDefault(name) ?? parent?.Find(name);
        public Cell Declare(string name, object? value) { var cell = new Cell(value); Cells[name] = cell; return cell; }
        public Dictionary<string, Cell> VisibleCells() { var cells = parent?.VisibleCells() ?? new Dictionary<string, Cell>(StringComparer.Ordinal); foreach (var pair in Cells) cells[pair.Key] = pair.Value; return cells; }
        public Scope Capture()
        {
            var scope = new Scope(Environment);
            void Copy(Scope frame) { if (ReferenceEquals(frame, Environment)) return; if (frame.parent is not null) Copy(frame.parent); foreach (var pair in frame.Cells) scope.Cells[pair.Key] = pair.Value; }
            Copy(this); return scope;
        }
    }
    private sealed record FunctionValue(FunctionExpr Definition, Scope Closure);
    private sealed record Native(string Name);
    private sealed record Iterator(TableValue Table, bool Pairs);
    private sealed class TableValue
    {
        public Dictionary<object, object?> Values { get; } = [];
        public bool Persistent;
        public int Version;
        public object? Get(object? key) => key is not null && Values.TryGetValue(key, out var value) ? value : null;
        public void Set(object? key, object? value)
        {
            if (key is null || key is double number && !double.IsFinite(number)) throw Unsupported("表索引不能为 nil 或非有限数值");
            if (!Values.ContainsKey(key) && Values.Count >= MaximumTableEntries) throw Unsupported("静态表超过 65,536 项");
            if (value is null) Values.Remove(key); else Values[key] = value; Version++;
        }
        public int ArrayLength(bool strict)
        {
            var count = 0; while (Values.ContainsKey((double)(count + 1))) count++;
            if (strict && Values.Keys.OfType<double>().Any(n => n > count && n > 0 && n == Math.Truncate(n))) throw Unsupported("有空洞的数组长度/遍历顺序不确定，请使用连续数字数组或明确下标");
            return count;
        }
    }
    private sealed class Compiler(List<Stmt> statements, int trigger, MacroRepeat requested, CancellationToken cancellationToken)
    {
        private readonly Scope globals = new(new Scope());
        private readonly List<string> press = [], hold = [], release = [];
        private List<string> output = [];
        private readonly HashSet<(string Kind, int Code)> held = [];
        private readonly HashSet<Cell> protectedCells = [];
        private readonly HashSet<TableValue> protectedTables = [];
        private readonly HashSet<string> topLocals = new(StringComparer.Ordinal);
        private bool initializing = true, inPhysicalLoop, hasPhysicalLoop, releaseEvent;
        private bool primaryEnabled = true;
        private int steps, depth;
        public LuaOnboardConversion Run()
        {
            InstallNatives();
            try { Execute(statements, globals); } catch (ReturnFlow) { throw Unsupported("顶层 return 不是 G HUB OnEvent 脚本"); }
            if (globals.Find("OnEvent")?.Value is not FunctionValue handler || handler.Definition.Parameters.Count < 2) throw new FormatException("Lua 需要定义 function OnEvent(event, arg)。");
            foreach (var cell in globals.VisibleCells().Values) { cell.Persistent = true; MarkPersistent(cell.Value, []); }
            initializing = false; AuditFunction(handler, [], 0);
            if (trigger == 1 && !primaryEnabled) throw Unsupported("脚本已关闭左键事件，不能生成会执行的左键宏");
            output = press; CallUser(handler, ["MOUSE_BUTTON_PRESSED", (double)trigger, "mouse"]);
            releaseEvent = true; output = release; CallUser(handler, ["MOUSE_BUTTON_RELEASED", (double)trigger, "mouse"]);
            if (press.Count + hold.Count + release.Count == 0) throw new FormatException("选定触发键的 Lua 按下/松开事件没有可转换的动作。");
            if (release.Count > 0 && requested is MacroRepeat.Toggle or MacroRepeat.WhileHeld) throw Unsupported("脚本包含松开动作，不能用开关宏/按住重复覆盖松开阶段；请选择单次或完整序列");
            var sequence = release.Count > 0 || hasPhysicalLoop && (press.Count > 0 || requested == MacroRepeat.Sequence);
            var repeat = sequence || requested == MacroRepeat.Sequence ? MacroRepeat.Sequence : hasPhysicalLoop && requested == MacroRepeat.Once ? MacroRepeat.WhileHeld : requested;
            if (hasPhysicalLoop && !sequence && repeat != MacroRepeat.Sequence) press.AddRange(hold);
            var result = new LuaOnboardConversion(string.Join('\n', press), repeat, trigger,
                "仅提取选定鼠标键的按下/松开事件；其他事件动作不写入板载。确定性计算已展开，未采样主机状态或随机数，不是安装 Lua 解释器。" + (hasPhysicalLoop ? "检测到选定按键的按住循环。" : ""))
            { HoldScript = repeat == MacroRepeat.Sequence ? string.Join('\n', hold) : "", ReleaseScript = string.Join('\n', release) };
            _ = result.CompileProgram(); return result;
        }
        private static void MarkPersistent(object? value, HashSet<TableValue> visited)
        { if (value is TableValue table && visited.Add(table)) { table.Persistent = true; foreach (var child in table.Values.Values) MarkPersistent(child, visited); } }
        private void Step()
        {
            if ((++steps & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (steps > 2000000) throw Unsupported("静态计算超过 2,000,000 步，循环未在限定范围内结束");
        }
        private void Execute(List<Stmt> body, Scope scope)
        {
            if (++depth > 32) throw Unsupported("函数/语句嵌套超过 32 层");
            try
            {
                foreach (var statement in body)
                {
                    Step();
                    switch (statement)
                    {
                        case Assign assignment:
                            var values = EvalList(assignment.Values, scope);
                            var targets = assignment.Targets.Select(t => ResolveTarget(t, scope, assignment.Local)).ToArray();
                            for (var i = 0; i < targets.Length; i++) Write(targets[i], i < values.Length ? values[i] : null, scope, assignment.Local);
                            break;
                        case Def definition:
                            if (definition.Local && definition.Target is Name localName)
                            { CheckTopLocal(localName.Value, scope); var cell = scope.Declare(localName.Value, null); cell.Value = new FunctionValue(definition.Function, scope.Capture()); }
                            else Write(ResolveTarget(definition.Target, scope, false), new FunctionValue(definition.Function, scope.Capture()), scope, false);
                            break;
                        case Invoke invoke: _ = CallAll(invoke.Call, scope); break;
                        case If condition:
                            var arm = condition.Arms.FirstOrDefault(a => Truth(Eval(a.Condition, scope))); Execute(arm.Body ?? condition.Else, new Scope(scope)); break;
                        case Block block: Execute(block.Body, new Scope(scope)); break;
                        case For loop:
                            var start = Number(Eval(loop.Start, scope)); var end = Number(Eval(loop.End, scope)); var increment = Number(Eval(loop.Step, scope));
                            if (increment == 0) throw Unsupported("for 循环步长不能为零");
                            for (var n = start; increment > 0 ? n <= end : n >= end; n += increment)
                            { Step(); var iteration = new Scope(scope); iteration.Declare(loop.Name, Finite(n)); try { Execute(loop.Body, iteration); } catch (BreakFlow) { break; } }
                            break;
                        case Each loop:
                            var iterators = EvalList(loop.Iterators, scope);
                            if (iterators.FirstOrDefault() is not Iterator iterator) throw Unsupported("generic for 仅支持 ipairs 或连续数字数组的 pairs");
                            var version = iterator.Table.Version; var length = iterator.Pairs ? iterator.Table.ArrayLength(true) : 0;
                            if (iterator.Pairs && iterator.Table.Values.Keys.Any(k => k is not double n || n < 1 || n > length || n != Math.Truncate(n))) throw Unsupported("pairs 仅支持连续数字数组；哈希表顺序不固定");
                            for (var n = 1; iterator.Pairs ? n <= length : iterator.Table.Get((double)n) is not null; n++)
                            {
                                Step(); if (iterator.Pairs && iterator.Table.Version != version) throw Unsupported("不能在 pairs 遍历时修改表");
                                var iteration = new Scope(scope);
                                for (var i = 0; i < loop.Names.Count; i++) iteration.Declare(loop.Names[i], i == 0 ? (double)n : i == 1 ? iterator.Table.Get((double)n) : null);
                                try { Execute(loop.Body, iteration); } catch (BreakFlow) { break; }
                            }
                            break;
                        case While loop:
                            if (SelectedHold(loop.Condition, scope, loop.Until)) { PhysicalLoop(loop, scope); break; }
                            if (loop.Until)
                            {
                                while (true) { Step(); var iteration = new Scope(scope); try { Execute(loop.Body, iteration); } catch (BreakFlow) { break; } if (Truth(Eval(loop.Condition, iteration))) break; }
                            }
                            else while (Truth(Eval(loop.Condition, scope))) { Step(); try { Execute(loop.Body, new Scope(scope)); } catch (BreakFlow) { break; } }
                            break;
                        case Return ret: throw new ReturnFlow(EvalList(ret.Values, scope));
                        case Break: throw new BreakFlow();
                    }
                }
            }
            finally { depth--; }
        }
        private void CheckTopLocal(string name, Scope scope) { if (initializing && ReferenceEquals(scope, globals) && !topLocals.Add(name)) throw Unsupported("顶层重复声明局部常量会产生不同闭包，需软件层执行"); }
        private object ResolveTarget(Expr target, Scope scope, bool local) => target switch
        { Name name => (object)(name.Value, local ? null : scope.Find(name.Value)), Index index when !local => (Table(Eval(index.Target, scope)), Eval(index.Key, scope)), _ => throw Unsupported("赋值目标不是变量或表字段") };
        private void Write(object target, object? value, Scope scope, bool local)
        {
            if (target is ValueTuple<string, Cell?> variable)
            {
                if (local) { CheckTopLocal(variable.Item1, scope); scope.Declare(variable.Item1, value); return; }
                var cell = variable.Item2;
                if (cell is null) { if (!initializing) throw Unsupported("会修改跨事件全局变量 " + variable.Item1); globals.Environment.Declare(variable.Item1, value); return; }
                if (!initializing && (cell.Persistent || protectedCells.Contains(cell))) throw Unsupported("会修改跨事件变量或按住循环外状态 " + variable.Item1);
                cell.Value = value;
            }
            else if (target is ValueTuple<TableValue, object?> indexed) { CheckMutable(indexed.Item1); indexed.Item1.Set(indexed.Item2, value); }
            else throw Unsupported("未知赋值目标");
        }
        private void CheckMutable(TableValue table) { if (!initializing && (table.Persistent || protectedTables.Contains(table))) throw Unsupported("配置表或其别名存在跨事件/按住轮次状态，需软件层执行"); }
        private bool SelectedHold(Expr expression, Scope scope, bool until)
        {
            if (until) { if (expression is not Unary { Op: "not" } negative) return false; expression = negative.Value; }
            return expression is Call { Target: Name { Value: "IsMouseButtonPressed" }, Arguments.Count: 1 } call && Integer(Eval(call.Arguments[0], scope), 1, 5) == trigger;
        }
        private void PhysicalLoop(While loop, Scope scope)
        {
            if (releaseEvent || initializing || inPhysicalLoop || hasPhysicalLoop) throw Unsupported("不能合并多个、嵌套或松开阶段的物理按住循环");
            hasPhysicalLoop = true; inPhysicalLoop = true; output = hold;
            foreach (var cell in scope.Capture().Cells.Values) { protectedCells.Add(cell); CollectTables(cell.Value, protectedTables); }
            try
            {
                try { Execute(loop.Body, new Scope(scope)); }
                catch (BreakFlow) { throw Unsupported("物理按住循环中的 break 不能表示为简单固件重复"); }
                catch (ReturnFlow) { throw Unsupported("物理按住循环中的提前 return 不能表示为简单固件重复"); }
            }
            finally { inPhysicalLoop = false; protectedCells.Clear(); protectedTables.Clear(); output = release; }
        }
        private static void CollectTables(object? value, HashSet<TableValue> tables) { if (value is TableValue table && tables.Add(table)) foreach (var child in table.Values.Values) CollectTables(child, tables); }
        private object? Eval(Expr expression, Scope scope)
        {
            Step();
            switch (expression)
            {
                case Literal literal: return literal.Value;
                case Name name: return scope.Find(name.Value)?.Value;
                case Grouped grouped: return Eval(grouped.Value, scope);
                case Index index: return Table(Eval(index.Target, scope)).Get(Eval(index.Key, scope));
                case FunctionExpr function: return new FunctionValue(function, scope.Capture());
                case TableExpr table:
                    var result = new TableValue(); var number = 1d;
                    for (var i = 0; i < table.Fields.Count; i++)
                    {
                        var field = table.Fields[i];
                        if (i == table.Fields.Count - 1 && field.Key is null && field.Value is Call call)
                        { foreach (var item in CallAll(call, scope)) result.Set(number++, item); }
                        else result.Set(field.Key is null ? number++ : Eval(field.Key, scope), Eval(field.Value, scope));
                    }
                    return result;
                case Unary unary:
                    var value = Eval(unary.Value, scope);
                    return unary.Op switch { "not" => !Truth(value), "-" => -Number(value), "#" => value is string text ? (double)Encoding.UTF8.GetByteCount(text) : (double)Table(value).ArrayLength(true), _ => throw Unsupported(unary.Op) };
                case Binary binary:
                    var left = Eval(binary.Left, scope);
                    if (binary.Op == "and") return Truth(left) ? Eval(binary.Right, scope) : left;
                    if (binary.Op == "or") return Truth(left) ? left : Eval(binary.Right, scope);
                    var right = Eval(binary.Right, scope);
                    return binary.Op switch
                    {
                        "==" => Equals(left, right), "~=" => !Equals(left, right), "+" => Finite(Number(left) + Number(right)), "-" => Finite(Number(left) - Number(right)),
                        "*" => Finite(Number(left) * Number(right)), "/" => Finite(Number(left) / Number(right)), "%" => Modulo(Number(left), Number(right)),
                        "^" => Finite(Math.Pow(Number(left), Number(right))), ".." => Concat(Concatenand(left), Concatenand(right)),
                        "<" => Compare(left, right) < 0, ">" => Compare(left, right) > 0, "<=" => Compare(left, right) <= 0, ">=" => Compare(left, right) >= 0,
                        _ => throw Unsupported("运算符 " + binary.Op),
                    };
                case Call call: return CallAll(call, scope).FirstOrDefault();
                default: throw Unsupported("无法静态计算的表达式");
            }
        }
        private static double Modulo(double left, double right)
        {
            if (right < 0) throw Unsupported("当前软件解释器与标准 Lua 对负除数 % 的语义不同；请用 a - math.floor(a/b)*b 明确写出标准取模公式，不能生成行为不同的板载宏");
            return Finite(left - Math.Floor(left / right) * right);
        }
        private object?[] EvalList(List<Expr> expressions, Scope scope)
        {
            var values = new List<object?>();
            for (var i = 0; i < expressions.Count; i++) if (i == expressions.Count - 1 && expressions[i] is Call call) values.AddRange(CallAll(call, scope)); else values.Add(Eval(expressions[i], scope));
            return values.ToArray();
        }
        private object?[] CallAll(Call call, Scope scope)
        {
            var function = Eval(call.Target, scope); var args = EvalList(call.Arguments, scope);
            return function switch { FunctionValue user => CallUser(user, args), Native native => CallNative(native.Name, args), _ => throw Unsupported("未知函数或主机接口 " + Describe(call.Target)) };
        }
        private object?[] CallUser(FunctionValue function, object?[] args)
        {
            var frame = new Scope(function.Closure);
            for (var i = 0; i < function.Definition.Parameters.Count; i++) frame.Declare(function.Definition.Parameters[i], i < args.Length ? args[i] : null);
            try { Execute(function.Definition.Body, frame); } catch (ReturnFlow ret) { return ret.Values; } catch (BreakFlow) { throw Unsupported("break 不能跳出函数边界"); } return [];
        }
        private object?[] CallNative(string name, object?[] args)
        {
            Step(); double N(int i) => i < args.Length ? Number(args[i]) : throw Unsupported(name + " 缺少参数"); object? A(int i) => i < args.Length ? args[i] : null;
            switch (name)
            {
                case "EnablePrimaryMouseButtonEvents":
                    if (args.Length != 1 || args[0] is not bool enabled) throw Unsupported("EnablePrimaryMouseButtonEvents 需要布尔参数");
                    if (initializing) primaryEnabled = enabled; else if (trigger == 1 && !enabled) throw Unsupported("选定左键事件被脚本关闭"); return [];
                case "OutputLogMessage" or "ClearLog": return [];
                case "Sleep": InputsOnly(name); var duration = Integer(A(0), 0, 600000); while (duration > 0) { var chunk = Math.Min(duration, 65535); Add("delay " + chunk); duration -= chunk; } return [];
                case "MoveMouseRelative": InputsOnly(name); Add($"move {Integer(A(0), short.MinValue, short.MaxValue)} {Integer(A(1), short.MinValue, short.MaxValue)}"); return [];
                case "MoveMouseWheel": InputsOnly(name); Add("wheel " + Integer(A(0), -127, 127)); return [];
                case "PressMouseButton" or "ReleaseMouseButton" or "PressAndReleaseMouseButton":
                    InputsOnly(name); if (args.Length == 0) throw Unsupported(name + " 缺少参数");
                    foreach (var argument in args) { var button = LuaKeyNames.MouseButton(Integer(argument, 1, 5)); if (name == "PressAndReleaseMouseButton") { if (!held.Contains(("mouse", button))) { Set("mouse", button, true); Set("mouse", button, false); } } else Set("mouse", button, name == "PressMouseButton"); } return [];
                case "PressKey" or "ReleaseKey" or "PressAndReleaseKey":
                    InputsOnly(name); if (args.Length == 0) throw Unsupported("键盘接口缺少参数");
                    var codes = args.SelectMany(a => LuaKeyNames.KeyCodes(a is string text ? DynValue.NewString(text) : a is double n ? DynValue.NewNumber(n) : throw Unsupported("键盘参数需为键名或扫描码"))).ToArray();
                    if (name == "PressAndReleaseKey") { var added = codes.Distinct().Where(code => !held.Contains(("key", code))).ToArray(); foreach (var code in added) Set("key", code, true); foreach (var code in added.Reverse()) Set("key", code, false); }
                    else foreach (var code in name == "ReleaseKey" ? codes.Reverse() : codes) Set("key", code, name == "PressKey"); return [];
                case "ipairs" or "pairs": return [new Iterator(Table(A(0)), name == "pairs")];
                case "type": return [A(0) switch { null => "nil", bool => "boolean", double => "number", string => "string", TableValue => "table", FunctionValue or Native => "function", _ => throw Unsupported("未知类型") }];
                case "tostring": return [Text(A(0))];
                case "tonumber":
                    if (A(0) is double direct) return [direct]; if (A(0) is not string numberText) return [null];
                    if (args.Length > 1) { var radix = Integer(A(1), 2, 36); try { return [(double)System.Convert.ToInt64(numberText, radix)]; } catch (ArgumentException) { throw Unsupported("tonumber 暂支持 2/8/10/16 进制"); } catch (FormatException) { return [null]; } }
                    return [double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null];
                case "assert": if (!Truth(A(0))) throw Unsupported("静态 assert 失败：" + (A(1) as string ?? "")); return args;
                case "error": throw Unsupported("静态 error：" + Text(A(0)));
                case "math.abs": return [Math.Abs(N(0))]; case "math.floor": return [Math.Floor(N(0))]; case "math.ceil": return [Math.Ceiling(N(0))];
                case "math.min": if (args.Length == 0) throw Unsupported("math.min 缺少参数"); return [args.Min(Number)];
                case "math.max": if (args.Length == 0) throw Unsupported("math.max 缺少参数"); return [args.Max(Number)];
                case "math.sqrt": return [Finite(Math.Sqrt(N(0)))]; case "math.pow": return [Finite(Math.Pow(N(0), N(1)))]; case "math.fmod": return [Finite(N(0) % N(1))];
                case "math.modf": var integral = Math.Truncate(N(0)); return [integral, N(0) - integral];
                case "math.deg": return [Finite(N(0) * 180 / Math.PI)]; case "math.rad": return [Finite(N(0) * Math.PI / 180)];
                case "math.sin": return [Math.Sin(N(0))]; case "math.cos": return [Math.Cos(N(0))]; case "math.tan": return [Finite(Math.Tan(N(0)))];
                case "math.asin": return [Finite(Math.Asin(N(0)))]; case "math.acos": return [Finite(Math.Acos(N(0)))]; case "math.atan": return [Math.Atan(N(0))]; case "math.atan2": return [Math.Atan2(N(0), N(1))];
                case "math.exp": return [Finite(Math.Exp(N(0)))]; case "math.log": return [Finite(args.Length > 1 ? Math.Log(N(0), N(1)) : Math.Log(N(0)))]; case "math.log10": return [Finite(Math.Log10(N(0)))];
                case "math.random" or "math.randomseed": throw Unsupported("随机数不能在转换时抽样固定；固件没有等价随机执行指令");
                case "table.insert":
                    var insert = Table(A(0)); CheckMutable(insert); var length = insert.ArrayLength(true); var position = args.Length == 2 ? length + 1 : Integer(A(1), 1, length + 1); var inserted = args.Length == 2 ? A(1) : A(2);
                    for (var n = length; n >= position; n--) { Step(); insert.Set((double)(n + 1), insert.Get((double)n)); } insert.Set((double)position, inserted); return [];
                case "table.remove":
                    var remove = Table(A(0)); CheckMutable(remove); var count = remove.ArrayLength(true); var removedIndex = args.Length == 1 ? count : Integer(A(1), 1, count);
                    if (count == 0) return [null]; var removed = remove.Get((double)removedIndex); for (var n = removedIndex; n < count; n++) { Step(); remove.Set((double)n, remove.Get((double)(n + 1))); } remove.Set((double)count, null); return [removed];
                case "table.concat":
                    var concat = Table(A(0)); var separator = args.Length > 1 ? Text(A(1)) : ""; var last = concat.ArrayLength(true); var first = args.Length > 2 ? Integer(A(2), 1, MaximumTableEntries) : 1; var final = args.Length > 3 ? Integer(A(3), 0, MaximumTableEntries) : last;
                    var combined = new StringBuilder();
                    for (var n = first; n <= final; n++)
                    {
                        Step(); if (n != first) combined.Append(separator); combined.Append(Text(concat.Get((double)n)));
                        if (combined.Length > 65536) throw Unsupported("静态字符串超过 65,536 字符");
                    }
                    return [combined.ToString()];
                case "unpack" or "table.unpack":
                    var unpack = Table(A(0)); var from = args.Length > 1 ? Integer(A(1), 1, MaximumTableEntries) : 1; var to = args.Length > 2 ? Integer(A(2), 0, MaximumTableEntries) : unpack.ArrayLength(true); return Enumerable.Range(from, Math.Max(0, to - from + 1)).Select(n => unpack.Get((double)n)).ToArray();
                default: throw Unsupported(name + " 依赖主机、时间或未知运行时状态，没有等价固件指令");
            }
        }
        private void InputsOnly(string name) { if (initializing) throw Unsupported("顶层初始化动作 " + name + " 不能改为鼠标事件触发动作"); }
        private void Set(string kind, int code, bool down)
        {
            if (down ? !held.Add((kind, code)) : !held.Remove((kind, code))) return;
            var key = code is >= 224 and <= 231 ? HidKeys.Describe((byte)(1 << (code - 224)), 0) : HidKeys.Describe(0, (byte)code);
            Add(kind == "mouse" ? $"mouse {code} {(down ? "down" : "up")}" : $"key {key} {(down ? "down" : "up")}");
        }
        private void Add(string line) { if (press.Count + hold.Count + release.Count >= 10000) throw Unsupported("宏指令超过 10,000 条"); output.Add(line); }
        private void InstallNatives()
        {
            foreach (var name in new[] { "EnablePrimaryMouseButtonEvents", "OutputLogMessage", "ClearLog", "Sleep", "MoveMouseRelative", "MoveMouseWheel", "PressMouseButton", "ReleaseMouseButton", "PressAndReleaseMouseButton", "PressKey", "ReleaseKey", "PressAndReleaseKey", "ipairs", "pairs", "type", "tostring", "tonumber", "assert", "error", "unpack", "IsMouseButtonPressed", "IsKeyLockOn", "IsModifierPressed", "GetRunningTime" }) globals.Environment.Declare(name, new Native(name));
            foreach (var (name, members) in new[] { ("math", "abs floor ceil min max sqrt pow fmod modf deg rad sin cos tan asin acos atan atan2 exp log log10 random randomseed"), ("table", "insert remove concat unpack") })
            { var library = new TableValue(); foreach (var member in members.Split(' ')) library.Set(member, new Native(name + "." + member)); if (name == "math") library.Set("pi", Math.PI); globals.Environment.Declare(name, library); }
        }
        private static bool Truth(object? value) => value is not null && value is not false;
        private static double Number(object? value) => value is double number && double.IsFinite(number) ? number : throw Unsupported("需要有限数值，不能把实时状态当作常量");
        private static double Finite(double value) => double.IsFinite(value) ? value : throw Unsupported("运算产生非有限值");
        private static int Integer(object? value, int minimum, int maximum) { var n = Number(value); if (n != Math.Truncate(n) || n < minimum || n > maximum) throw Unsupported($"参数需要 {minimum}–{maximum} 之间的整数"); return (int)n; }
        private static TableValue Table(object? value) => value as TableValue ?? throw Unsupported("表达式不是可静态确定的表");
        private static int Compare(object? left, object? right) => left is string a && right is string b ? string.CompareOrdinal(a, b) : Number(left).CompareTo(Number(right));
        private static string Text(object? value) => value switch { null => "nil", true => "true", false => "false", string text => text, double n => Number(n).ToString("G14", CultureInfo.InvariantCulture), _ => throw Unsupported("表/函数的地址字符串不确定") };
        private static string Concatenand(object? value) => value is string or double ? Text(value) : throw Unsupported("Lua 字符串拼接只接受字符串/数值，布尔值和 nil 需先 tostring");
        private static string Concat(string left, string right) => left.Length + right.Length <= 65536 ? left + right : throw Unsupported("静态字符串超过 65,536 字符");
        private static string Describe(Expr expression) => expression switch { Name name => name.Value, Index index when index.Key is Literal { Value: string key } => Describe(index.Target) + "." + key, _ => "表达式" };
        private sealed class Symbol(bool persistent, bool table = false, FunctionValue? function = null)
        { public bool Persistent = persistent; public bool Table = table; public FunctionValue? Function = function; public FunctionExpr? LocalFunction; public Dictionary<string, Symbol>? Closure; public bool Native; public TableValue? RuntimeTable; }
        private void AuditFunction(FunctionValue function, List<bool> args, int level)
        {
            if (level > 24) throw Unsupported("递归辅助函数的状态无法静态确认");
            var symbols = function.Closure.VisibleCells().ToDictionary(p => p.Key, p => new Symbol(p.Value.Persistent, p.Value.Value is TableValue t && t.Persistent, p.Value.Value as FunctionValue) { Native = p.Value.Value is Native, RuntimeTable = p.Value.Value as TableValue }, StringComparer.Ordinal);
            for (var i = 0; i < function.Definition.Parameters.Count; i++) symbols[function.Definition.Parameters[i]] = new(false, i < args.Count && args[i]); AuditBlock(function.Definition.Body, symbols, level);
        }
        private void AuditBlock(List<Stmt> body, Dictionary<string, Symbol> symbols, int level)
        {
            Step(); if (level > 32) throw Unsupported("静态状态分析嵌套过深");
            bool Alias(Expr expr) => expr switch
            {
                Name n => symbols.TryGetValue(n.Value, out var s) && s.Table,
                Grouped g => Alias(g.Value), Index i => Alias(i.Target), TableExpr t => t.Fields.Any(f => Alias(f.Value)),
                Call c when c.Target is Name n && symbols.TryGetValue(n.Value, out var s) && s.Function is not null => ReturnedAlias(s.Function.Definition, s.Function.Closure.VisibleCells().ToDictionary(p => p.Key, p => p.Value.Value is TableValue table && table.Persistent, StringComparer.Ordinal), c.Arguments.Select(Alias).ToArray(), 0),
                Call c when c.Target is Name n && symbols.TryGetValue(n.Value, out var s) && s.LocalFunction is not null && s.Closure is not null => ReturnedAlias(s.LocalFunction, s.Closure.ToDictionary(p => p.Key, p => p.Value.Table, StringComparer.Ordinal), c.Arguments.Select(Alias).ToArray(), 0),
                Call c => c.Arguments.Any(Alias), _ => false,
            };
            void Expression(Expr expr)
            {
                switch (expr)
                {
                    case Call call:
                        if (trigger == 1 && Describe(call.Target) == "EnablePrimaryMouseButtonEvents" && call.Arguments is not [Literal { Value: true }]) throw Unsupported("其他事件可能关闭左键监听，不能生成始终执行的左键板载宏");
                        if (Describe(call.Target) is "rawset" or "setmetatable" or "load" or "loadstring" or "dofile" or "require") throw Unsupported("事件包含动态 Lua 环境写入/加载，跨事件状态无法静态确认");
                        if (Describe(call.Target) is "table.insert" or "table.remove" && call.Arguments.Count > 0 && Alias(call.Arguments[0])) throw Unsupported("其他事件可能通过 table 函数修改配置表/别名，不能固定快照");
                        if (call.Target is Name name && symbols.TryGetValue(name.Value, out var symbol))
                        {
                            if (symbol.Function is not null) AuditFunction(symbol.Function, call.Arguments.Select(Alias).ToList(), level + 1);
                            else if (symbol.LocalFunction is not null && symbol.Closure is not null)
                            { var locals = new Dictionary<string, Symbol>(symbol.Closure, StringComparer.Ordinal); for (var i = 0; i < symbol.LocalFunction.Parameters.Count; i++) locals[symbol.LocalFunction.Parameters[i]] = new(false, i < call.Arguments.Count && Alias(call.Arguments[i])); AuditBlock(symbol.LocalFunction.Body, locals, level + 1); }
                            else if (!symbol.Native) throw Unsupported("动态函数别名 " + name.Value + " 的跨事件写入无法静态确认");
                        }
                        else if (call.Target is Index { Target: Name tableName, Key: Literal { Value: string field } } && symbols.TryGetValue(tableName.Value, out var tableSymbol) && tableSymbol.RuntimeTable?.Get(field) is object member)
                        { if (member is FunctionValue function) AuditFunction(function, call.Arguments.Select(Alias).ToList(), level + 1); else if (member is not Native) throw Unsupported("动态表函数的跨事件状态无法静态确认"); }
                        else throw Unsupported("动态函数调用的跨事件状态无法静态确认");
                        foreach (var arg in call.Arguments) Expression(arg); break;
                    case Binary b: Expression(b.Left); Expression(b.Right); break;
                    case Unary u: Expression(u.Value); break;
                    case Index i: Expression(i.Target); Expression(i.Key); break;
                    case Grouped g: Expression(g.Value); break;
                    case FunctionExpr f:
                        var functionSymbols = new Dictionary<string, Symbol>(symbols, StringComparer.Ordinal);
                        foreach (var parameter in f.Parameters) functionSymbols[parameter] = new(false);
                        AuditBlock(f.Body, functionSymbols, level + 1); break;
                    case TableExpr t: foreach (var field in t.Fields) { if (field.Key is not null) Expression(field.Key); Expression(field.Value); } break;
                }
            }
            foreach (var statement in body)
            {
                Step();
                switch (statement)
                {
                    case Assign assignment:
                        foreach (var value in assignment.Values) Expression(value);
                        for (var i = 0; i < assignment.Targets.Count; i++)
                        {
                            var target = assignment.Targets[i];
                            var alias = i < assignment.Values.Count ? Alias(assignment.Values[i]) : assignment.Values.LastOrDefault() is Call tail && Alias(tail);
                        if (target is Name variableName)
                            {
                                if (assignment.Local)
                                {
                                    var declared = new Symbol(false, alias); symbols[variableName.Value] = declared;
                                    if (i < assignment.Values.Count && assignment.Values[i] is FunctionExpr localFunction) { declared.LocalFunction = localFunction; declared.Closure = new(symbols, StringComparer.Ordinal); }
                                }
                                else if (!symbols.TryGetValue(variableName.Value, out var symbol) || symbol.Persistent) throw Unsupported("其他事件可能修改跨事件变量 " + variableName.Value);
                                else symbol.Table |= alias;
                            }
                            else if (target is Index index && Alias(index.Target)) throw Unsupported("其他事件可能修改配置表/别名，不能固定当前表值");
                        }
                        break;
                    case Def definition:
                        if (definition.Target is Name name)
                        {
                            if (!definition.Local && (!symbols.TryGetValue(name.Value, out var symbol) || symbol.Persistent)) throw Unsupported("事件内修改全局函数 " + name.Value);
                            var local = new Symbol(false) { LocalFunction = definition.Function }; symbols[name.Value] = local; local.Closure = new(symbols, StringComparer.Ordinal);
                        }
                        break;
                    case Invoke invoke: Expression(invoke.Call); break;
                    case If condition: foreach (var arm in condition.Arms) { Expression(arm.Condition); AuditBlock(arm.Body, new(symbols, StringComparer.Ordinal), level + 1); } AuditBlock(condition.Else, new(symbols, StringComparer.Ordinal), level + 1); break;
                    case Block block: AuditBlock(block.Body, new(symbols, StringComparer.Ordinal), level + 1); break;
                    case For loop: Expression(loop.Start); Expression(loop.End); Expression(loop.Step); var forSymbols = new Dictionary<string, Symbol>(symbols, StringComparer.Ordinal) { [loop.Name] = new(false) }; AuditBlock(loop.Body, forSymbols, level + 1); break;
                    case Each loop:
                        foreach (var iterator in loop.Iterators) Expression(iterator);
                        var eachSymbols = new Dictionary<string, Symbol>(symbols, StringComparer.Ordinal);
                        var persistentElements = loop.Iterators.Any(i => i is Call c && c.Arguments.Any(Alias));
                        for (var i = 0; i < loop.Names.Count; i++) eachSymbols[loop.Names[i]] = new(false, i > 0 && persistentElements);
                        AuditBlock(loop.Body, eachSymbols, level + 1); break;
                    case While loop: Expression(loop.Condition); AuditBlock(loop.Body, new(symbols, StringComparer.Ordinal), level + 1); break;
                    case Return ret: foreach (var value in ret.Values) Expression(value); break;
                }
            }
        }
        private bool ReturnedAlias(FunctionExpr function, Dictionary<string, bool> captured, bool[] arguments, int level)
        {
            if (level > 12) return true;
            var bindings = new Dictionary<string, bool>(captured, StringComparer.Ordinal);
            for (var i = 0; i < function.Parameters.Count; i++) bindings[function.Parameters[i]] = i < arguments.Length && arguments[i];
            bool Tainted(Expr expression, Dictionary<string, bool> names) => expression switch
            {
                Name n => names.GetValueOrDefault(n.Value), Grouped g => Tainted(g.Value, names), Index i => Tainted(i.Target, names),
                TableExpr t => t.Fields.Any(f => Tainted(f.Value, names)),
                Call c when c.Target is Name n && globals.Find(n.Value)?.Value is FunctionValue helper => ReturnedAlias(helper.Definition, helper.Closure.VisibleCells().ToDictionary(p => p.Key, p => p.Value.Value is TableValue table && table.Persistent, StringComparer.Ordinal), c.Arguments.Select(a => Tainted(a, names)).ToArray(), level + 1),
                Call c when c.Target is Index { Target: Name { Value: "math" } } => false,
                Call c when c.Target is Name n && globals.Find(n.Value)?.Value is Native => c.Arguments.Any(a => Tainted(a, names)),
                Call => true, _ => false,
            };
            bool Visit(List<Stmt> body, Dictionary<string, bool> names)
            {
                if (++level > 24) return true;
                try
                {
                    var returns = false;
                    foreach (var statement in body)
                    {
                        Step();
                        switch (statement)
                        {
                            case Return ret: returns |= ret.Values.Any(v => Tainted(v, names)); break;
                            case Assign assignment:
                                for (var i = 0; i < assignment.Targets.Count; i++)
                                {
                                    var alias = i < assignment.Values.Count ? Tainted(assignment.Values[i], names) : assignment.Values.LastOrDefault() is Call tail && Tainted(tail, names);
                                    if (assignment.Targets[i] is Name name) names[name.Value] = alias;
                                    else if (assignment.Targets[i] is Index { Target: Name tableName }) names[tableName.Value] = names.GetValueOrDefault(tableName.Value) || alias;
                                }
                                break;
                            case If condition:
                                foreach (var arm in condition.Arms) returns |= Branch(arm.Body, names);
                                returns |= Branch(condition.Else, names); break;
                            case Block block: returns |= Branch(block.Body, names); break;
                            case For loop: returns |= Branch(loop.Body, names); break;
                            case Each loop: returns |= Branch(loop.Body, names); break;
                            case While loop: returns |= Branch(loop.Body, names); break;
                        }
                    }
                    return returns;
                }
                finally { level--; }
            }
            bool Branch(List<Stmt> body, Dictionary<string, bool> names)
            {
                var branch = new Dictionary<string, bool>(names, StringComparer.Ordinal);
                var returned = Visit(body, branch);
                var shadows = body.OfType<Assign>().Where(a => a.Local).SelectMany(a => a.Targets.OfType<Name>().Select(n => n.Value)).ToHashSet(StringComparer.Ordinal);
                foreach (var name in names.Keys.Where(n => !shadows.Contains(n)).ToArray()) names[name] |= branch.GetValueOrDefault(name);
                return returned;
            }
            return Visit(function.Body, bindings);
        }
    }
    private sealed record Token(string Text, object? Value, int Line);
    private sealed class Parser
    {
        private readonly List<Token> tokens;
        private int position, nesting;
        private readonly CancellationToken cancellationToken;
        public Parser(string source, CancellationToken token) { cancellationToken = token; tokens = Lex(source, token); }
        private Token Current { get { cancellationToken.ThrowIfCancellationRequested(); return tokens[position]; } }
        private Token Next => tokens[Math.Min(position + 1, tokens.Count - 1)];
        private bool Take(string text) { if (Current.Text != text) return false; position++; return true; }
        private void Need(string text) { if (!Take(text)) throw Error("需要 " + text); }
        private FormatException Error(string detail) => new($"Lua 第 {Current.Line} 行：{detail}（遇到 {Current.Text}）。");
        private string Identifier() { if (Current.Value is not string || Current.Text.StartsWith('"')) throw Error("需要名称"); return tokens[position++].Text; }
        public List<Stmt> Parse() { var body = Statements("<eof>"); Need("<eof>"); return body; }
        private List<Stmt> Statements(params string[] endings)
        {
            if (++nesting > 64) throw Error("语法嵌套超过 64 层");
            try
            {
                var body = new List<Stmt>();
                while (!endings.Contains(Current.Text))
                {
                    if (Current.Text == "<eof>") throw Error("语句块缺少 end/until"); if (Take(";")) continue;
                    var local = Take("local");
                    if (Take("function")) { Expr target = new Name(Identifier()); while (!local && Take(".")) target = new Index(target, new Literal(Identifier())); body.Add(new Def(target, FunctionBody(), local)); }
                    else if (local) { var targets = new List<Expr> { new Name(Identifier()) }; while (Take(",")) targets.Add(new Name(Identifier())); body.Add(new Assign(targets, Take("=") ? Expressions() : [], true)); }
                    else if (Take("if")) { var arms = new List<(Expr, List<Stmt>)>(); do { var condition = Expression(); Need("then"); arms.Add((condition, Statements("elseif", "else", "end"))); } while (Take("elseif")); var otherwise = Take("else") ? Statements("end") : []; Need("end"); body.Add(new If(arms, otherwise)); }
                    else if (Take("do")) { var block = Statements("end"); Need("end"); body.Add(new Block(block)); }
                    else if (Take("for"))
                    {
                        var name = Identifier();
                        if (Take("=")) { var start = Expression(); Need(","); var end = Expression(); var step = Take(",") ? Expression() : new Literal(1d); Need("do"); var block = Statements("end"); Need("end"); body.Add(new For(name, start, end, step, block)); }
                        else { var names = new List<string> { name }; while (Take(",")) names.Add(Identifier()); Need("in"); var iterators = Expressions(); Need("do"); var block = Statements("end"); Need("end"); body.Add(new Each(names, iterators, block)); }
                    }
                    else if (Take("while")) { var condition = Expression(); Need("do"); var block = Statements("end"); Need("end"); body.Add(new While(condition, block, false)); }
                    else if (Take("repeat")) { var block = Statements("until"); Need("until"); body.Add(new While(Expression(), block, true)); }
                    else if (Take("return")) { body.Add(new Return(Current.Text is "end" or "else" or "elseif" or "until" or ";" or "<eof>" ? [] : Expressions())); Take(";"); }
                    else if (Take("break")) body.Add(new Break());
                    else { var first = Prefix(); if (Current.Text is "=" or ",") { var targets = new List<Expr> { first }; while (Take(",")) targets.Add(Prefix()); Need("="); body.Add(new Assign(targets, Expressions(), false)); } else if (first is Call call) body.Add(new Invoke(call)); else throw Error("需要赋值或函数调用"); }
                }
                return body;
            }
            finally { nesting--; }
        }
        private FunctionExpr FunctionBody() { Need("("); var parameters = new List<string>(); if (!Take(")")) { do { parameters.Add(Identifier()); } while (Take(",")); Need(")"); } var body = Statements("end"); Need("end"); return new(parameters, body); }
        private List<Expr> Expressions() { var values = new List<Expr> { Expression() }; while (Take(",")) values.Add(Expression()); return values; }
        private static int Priority(string op) => op switch { "or" => 1, "and" => 2, "==" or "~=" or "<" or ">" or "<=" or ">=" => 3, ".." => 4, "+" or "-" => 5, "*" or "/" or "%" => 6, "^" => 8, _ => 0 };
        private Expr Expression(int minimum = 1)
        {
            if (++nesting > 64) throw Error("表达式嵌套超过 64 层");
            try
            {
                Expr left;
                if (Current.Text is "not" or "-" or "#") { var op = tokens[position++].Text; left = new Unary(op, Expression(7)); }
                else if (Take("true")) left = new Literal(true); else if (Take("false")) left = new Literal(false); else if (Take("nil")) left = new Literal(null); else if (Take("function")) left = FunctionBody();
                else if (Take("{"))
                { var fields = new List<(Expr?, Expr)>(); while (!Take("}")) { Expr? key = null; if (Take("[")) { key = Expression(); Need("]"); Need("="); } else if (Current.Value is string && !Current.Text.StartsWith('"') && Next.Text == "=") { key = new Literal(Identifier()); Need("="); } fields.Add((key, Expression())); if (!Take(",") && !Take(";")) { Need("}"); break; } } left = new TableExpr(fields); }
                else if (Current.Value is double || Current.Text.StartsWith('"')) left = new Literal(tokens[position++].Value); else left = Prefix();
                while (Priority(Current.Text) >= minimum) { var op = tokens[position++].Text; left = new Binary(op, left, Expression(Priority(op) + (op is "^" or ".." ? 0 : 1))); } return left;
            }
            finally { nesting--; }
        }
        private Expr Prefix()
        {
            Expr value; if (Take("(")) { value = new Grouped(Expression()); Need(")"); } else value = new Name(Identifier());
            while (true)
            { if (Take(".")) value = new Index(value, new Literal(Identifier())); else if (Take("[")) { var key = Expression(); Need("]"); value = new Index(value, key); } else if (Take("(")) { List<Expr> args = []; if (!Take(")")) { args = Expressions(); Need(")"); } value = new Call(value, args); } else break; } return value;
        }
        private static readonly Regex NumberToken = new(@"\G(?:0[xX][0-9a-fA-F]+|(?:\d+(?:\.(?!\.)\d*)?|\.\d+)(?:[eE][+-]?\d+)?)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static List<Token> Lex(string source, CancellationToken cancellationToken)
        {
            var tokens = new List<Token>(); var index = 0; var line = 1;
            bool LongString(int start, out int equals, out int openEnd) { equals = 0; openEnd = start; if (source[start] != '[') return false; var p = start + 1; while (p < source.Length && source[p] == '=') { equals++; p++; } if (p >= source.Length || source[p] != '[') return false; openEnd = p + 1; return true; }
            while (index < source.Length)
            {
                if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (tokens.Count > 1000000) throw Unsupported("语法单元超过 1,000,000 项");
                var c = source[index]; if (char.IsWhiteSpace(c)) { if (c == '\n') line++; index++; continue; }
                var comment = c == '-' && index + 1 < source.Length && source[index + 1] == '-'; if (comment) index += 2;
                if (index < source.Length && LongString(index, out var equals, out var openEnd)) { var delimiter = "]" + new string('=', equals) + "]"; var end = source.IndexOf(delimiter, openEnd, StringComparison.Ordinal); if (end < 0) throw new FormatException("Lua 长字符串/注释未结束。"); var value = source[openEnd..end]; if (value.StartsWith("\r\n")) value = value[2..]; else if (value.StartsWith('\n')) value = value[1..]; if (!comment) tokens.Add(new("\"" + value, value, line)); line += source[index..(end + delimiter.Length)].Count(ch => ch == '\n'); index = end + delimiter.Length; continue; }
                if (comment) { while (index < source.Length && source[index] != '\n') index++; continue; }
                if (c is '\'' or '"')
                {
                    var quote = c; var text = new StringBuilder(); var origin = line; index++;
                    while (index < source.Length && source[index] != quote)
                    {
                        var ch = source[index++];
                        if (ch == '\\')
                        {
                            if (index == source.Length) throw new FormatException("Lua 字符串转义未结束。"); ch = source[index++];
                            if (ch == 'z') { while (index < source.Length && char.IsWhiteSpace(source[index])) { if (source[index++] == '\n') line++; } continue; }
                            if (char.IsDigit(ch)) { var digits = ch.ToString(); while (digits.Length < 3 && index < source.Length && char.IsDigit(source[index])) digits += source[index++]; var code = int.Parse(digits, CultureInfo.InvariantCulture); if (code > 255) throw new FormatException("Lua 转义超出字节范围。"); ch = (char)code; }
                            else if (ch == 'x') { if (index + 2 > source.Length || !byte.TryParse(source.AsSpan(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) throw new FormatException("Lua 十六进制转义无效。"); ch = (char)code; index += 2; }
                            else ch = ch switch { 'a' => '\a', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', 'v' => '\v', '\\' => '\\', '\'' => '\'', '"' => '"', '\n' => '\n', _ => throw new FormatException("Lua 字符串转义不受支持。") };
                        }
                        if (ch == '\n') line++; text.Append(ch);
                    }
                    if (index == source.Length) throw new FormatException("Lua 字符串未结束。"); index++; tokens.Add(new("\"" + text, text.ToString(), origin)); continue;
                }
                if (char.IsLetter(c) || c == '_') { var start = index++; while (index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] == '_')) index++; var name = source[start..index]; tokens.Add(new(name, name, line)); continue; }
                if (char.IsDigit(c) || c == '.' && index + 1 < source.Length && char.IsDigit(source[index + 1]))
                { var match = NumberToken.Match(source, index); if (!match.Success) throw new FormatException("Lua 数值常量无效。"); var raw = match.Value; index += raw.Length; var number = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? (double)long.Parse(raw.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : double.Parse(raw, CultureInfo.InvariantCulture); if (!double.IsFinite(number)) throw new FormatException("Lua 数值不是有限数。"); tokens.Add(new(raw, number, line)); continue; }
                var symbol = c.ToString(); if (index + 1 < source.Length && source.Substring(index, 2) is "==" or "~=" or "<=" or ">=" or "..") symbol = source.Substring(index, 2);
                if (!"(){}[]+-*/%^#.,;=<>~".Contains(c)) throw new FormatException($"Lua 第 {line} 行：板载转换不支持符号 {c}。"); tokens.Add(new(symbol, null, line)); index += symbol.Length;
            }
            tokens.Add(new("<eof>", null, line)); return tokens;
        }
    }
}

public sealed record LuaOnboardConversion(string Script, MacroRepeat Repeat, int TriggerButton, string Notice)
{
    internal MacroProgram? CanonicalProgram { get; init; }
    public string PressScript => Script;
    public string HoldScript { get; init; } = "";
    public string ReleaseScript { get; init; } = "";
    public MacroProgram CompileProgram() => CanonicalProgram ?? MacroCompiler.CompileProgram(Script, Repeat, HoldScript, ReleaseScript);
}
