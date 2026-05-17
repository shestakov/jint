using Jint.Native;
using Jint.Runtime.Interpreter;

namespace Jint.Runtime.Debugger;

public enum PauseType
{
    Skip,
    Step,
    Break,
    DebuggerStatement,
    Exception,
}

public class DebugHandler
{
    public delegate void BeforeEvaluateEventHandler(object sender, Program ast);
    public delegate StepMode DebugEventHandler(object sender, DebugInformation e);

    private readonly Engine _engine;
    private bool _paused;
    private int _steppingDepth;

    /// <summary>
    /// Triggered before the engine executes/evaluates the parsed AST of a script or module.
    /// </summary>
    public event BeforeEvaluateEventHandler? BeforeEvaluate;

    /// <summary>
    /// The Step event is triggered before the engine executes a step-eligible execution point.
    /// </summary>
    /// <remarks>
    /// If the current step mode is <see cref="StepMode.None"/>, this event is never triggered. The script may
    /// still be paused by a debugger statement or breakpoint, but these will trigger the
    /// <see cref="Break"/> event.
    /// </remarks>
    public event DebugEventHandler? Step;

    /// <summary>
    /// The Break event is triggered when a breakpoint or debugger statement is hit.
    /// </summary>
    /// <remarks>
    /// This is event is not triggered if the current script location was reached by stepping. In that case, only
    /// the <see cref="Step"/> event is triggered.
    /// </remarks>
    public event DebugEventHandler? Break;


    /// <summary>
    /// The Skip event is triggered for each execution point, when the point doesn't trigger a <see cref="Step"/>
    /// or <see cref="Break"/> event.
    /// </summary>
    public event DebugEventHandler? Skip;

    /// <summary>
    /// Triggered before a JavaScript <c>throw</c> statement propagates its
    /// thrown value. The handler's <see cref="DebugInformation.Exception"/>
    /// holds the value. The handler returns a <see cref="StepMode"/> just
    /// like other debug events; <see cref="StepMode.None"/> resumes the throw.
    /// </summary>
    public event DebugEventHandler? Exception;

    internal DebugHandler(Engine engine, StepMode initialStepMode)
    {
        _engine = engine;
        HandleNewStepMode(initialStepMode);
    }

    /// <summary>
    /// Schedules the engine to pause on the next step-eligible execution
    /// point — equivalent to <see cref="StepMode.Into"/> taking effect
    /// immediately. Safe to call from a thread other than the engine thread.
    /// </summary>
    public void RequestPause()
    {
        // Volatile write so the engine thread observes the new depth on its
        // next IsStepping check without needing a memory barrier.
        System.Threading.Volatile.Write(ref _steppingDepth, int.MaxValue);
    }

    // ===== Try-block depth tracking (for pause-on-uncaught-exception) =====
    // Incremented when a JintTryStatement begins executing its try-block,
    // decremented when the block exits (either normally or via a throw that
    // the catch will handle). A throw whose depth is 0 is considered uncaught.
    // The counter does NOT include the catch / finally blocks — that's the
    // whole point: a re-throw from inside `catch` reads the OUTER tries,
    // matching the spec's "uncaught" semantics.
    private int _tryBlockDepth;

    internal void EnterTryBlock() => _tryBlockDepth++;
    internal void ExitTryBlock() => _tryBlockDepth--;
    internal bool IsInsideTryBlock => _tryBlockDepth > 0;

    private bool IsStepping => _engine.CallStack.Count <= _steppingDepth;

    /// <summary>
    /// The location of the current (step-eligible) AST node being executed.
    /// </summary>
    /// <remarks>
    /// The location is available as long as DebugMode is enabled - i.e. even when not stepping
    /// or hitting a breakpoint.
    /// </remarks>
    public SourceLocation? CurrentLocation { get; private set; }

    /// <summary>
    /// Collection of active breakpoints for the engine.
    /// </summary>
    public BreakPointCollection BreakPoints { get; } = new BreakPointCollection();

    /// <summary>
    /// Evaluates a script (expression) within the current execution context.
    /// </summary>
    /// <remarks>
    /// Internally, this is used for evaluating breakpoint conditions, but may also be used for e.g. watch lists
    /// in a debugger.
    /// </remarks>
    public JsValue Evaluate(in Prepared<Script> preparedScript)
    {
        if (!preparedScript.IsValid)
        {
            Throw.InvalidPreparedScriptArgumentException(nameof(preparedScript));
        }

        var context = _engine._activeEvaluationContext;
        if (context == null)
        {
            throw new DebugEvaluationException("Jint has no active evaluation context");
        }
        var callStackSize = _engine.CallStack.Count;
        // Save the entire execution context. If the watch / breakpoint
        // condition enters a function call or any nested environment, a
        // CLR-side throw mid-execution would leave the engine pointing at a
        // stale LexicalEnvironment / VariableEnvironment / PrivateEnvironment,
        // and the next FunctionDeclarationInstantiation cast to FunctionEnvironment
        // would fail. We restore the snapshot in `finally`.
        var savedContext = _engine.ExecutionContext;

        var list = new JintStatementList(null, preparedScript.Program.Body);
        Completion result;
        try
        {
            result = list.Execute(context);
        }
        catch (Exception ex)
        {
            // An error in the evaluation may return a Throw Completion, or it may throw an exception:
            throw new DebugEvaluationException("An error occurred during debugger evaluation", ex);
        }
        finally
        {
            // Restore call stack
            while (_engine.CallStack.Count > callStackSize)
            {
                _engine.CallStack.Pop();
            }
            // Restore the lexical / variable / private environments. A
            // CLR-side throw mid-evaluation (e.g. inside an inner function
            // call) would otherwise leave a stale environment on the
            // ExecutionContext, breaking subsequent FunctionDeclarationInstantiation
            // (which casts LexicalEnvironment to FunctionEnvironment).
            _engine.UpdateLexicalEnvironment(savedContext.LexicalEnvironment);
            _engine.UpdateVariableEnvironment(savedContext.VariableEnvironment);
            _engine.UpdatePrivateEnvironment(savedContext.PrivateEnvironment);
        }

        if (result.Type == CompletionType.Throw)
        {
            // TODO: Should we return an error here? (avoid exception overhead, since e.g. breakpoint
            // evaluation may be high volume.
            var error = result.GetValueOrDefault();
            var ex = new JavaScriptException(error).SetJavaScriptCallstack(_engine, result.Location);
            throw new DebugEvaluationException("An error occurred during debugger evaluation", ex);
        }

        return result.GetValueOrDefault();
    }

    /// <inheritdoc cref="Evaluate(in Prepared{Script})" />
    public JsValue Evaluate(string sourceText, ScriptParsingOptions? parsingOptions = null)
    {
        var parserOptions = parsingOptions?.GetParserOptions() ?? _engine.GetActiveParserOptions();
        var parser = _engine.GetParserFor(parserOptions);
        try
        {
            var script = parser.ParseScript(sourceText, "evaluation");
            return Evaluate(new Prepared<Script>(script, parserOptions));
        }
        catch (ParseErrorException ex)
        {
            throw new DebugEvaluationException("An error occurred during debugger expression parsing", ex);
        }
    }

    internal void OnBeforeEvaluate(Program ast)
    {
        if (ast != null)
        {
            BeforeEvaluate?.Invoke(_engine, ast);
        }
    }

    internal void OnStep(Node node)
    {
        // Don't reenter if we're already paused (e.g. when evaluating a getter in a Break/Step handler)
        if (_paused)
        {
            return;
        }
        _paused = true;

        CheckBreakPointAndPause(node, node.Location);
    }

    internal void OnReturnPoint(Node functionBody, JsValue returnValue)
    {
        // Don't reenter if we're already paused (e.g. when evaluating a getter in a Break/Step handler)
        if (_paused)
        {
            return;
        }
        _paused = true;

        var bodyLocation = functionBody.Location;
        var functionBodyEnd = bodyLocation.End;
        var location = SourceLocation.From(functionBodyEnd, functionBodyEnd, bodyLocation.SourceFile);

        CheckBreakPointAndPause(node: null, location, returnValue);
    }

    private void CheckBreakPointAndPause(
        Node? node,
        in SourceLocation location,
        JsValue? returnValue = null)
    {
        CurrentLocation = location;

        // Even if we matched a breakpoint, if we're stepping, the reason we're pausing is the step.
        // Still, we need to include the breakpoint at this location, in case the debugger UI needs to update
        // e.g. a hit count.
        var breakLocation = new BreakLocation(location.SourceFile, location.Start);
        var breakPoint = BreakPoints.FindMatch(this, breakLocation);

        PauseType pauseType;

        if (IsStepping)
        {
            pauseType = PauseType.Step;
        }
        else if (breakPoint != null)
        {
            pauseType = PauseType.Break;
        }
        else if (node?.Type == NodeType.DebuggerStatement &&
                 _engine.Options.Debugger.StatementHandling == DebuggerStatementHandling.Script)
        {
            pauseType = PauseType.DebuggerStatement;
        }
        else
        {
            pauseType = PauseType.Skip;
        }

        Pause(pauseType, node, location, returnValue, breakPoint);

        _paused = false;
    }

    private void Pause(
        PauseType type,
        Node? node,
        in SourceLocation location,
        JsValue? returnValue = null,
        BreakPoint? breakPoint = null)
    {
        var info = new DebugInformation(
            engine: _engine,
            currentNode: node,
            currentLocation: location,
            returnValue: returnValue,
            currentMemoryUsage: _engine.CurrentMemoryUsage,
            pauseType: type,
            breakPoint: breakPoint
        );

        StepMode? result = type switch
        {
            // Conventionally, sender should be DebugHandler - but Engine is more useful
            PauseType.Skip => Skip?.Invoke(_engine, info),
            PauseType.Step => Step?.Invoke(_engine, info),
            PauseType.Break => Break?.Invoke(_engine, info),
            PauseType.DebuggerStatement => Break?.Invoke(_engine, info),
            PauseType.Exception => Exception?.Invoke(_engine, info),
            _ => throw new ArgumentException("Invalid pause type", nameof(type))
        };

        HandleNewStepMode(result);
    }

    /// <summary>
    /// Called by the interpreter when a JavaScript <c>throw</c> statement is
    /// about to propagate. Fires the <see cref="Exception"/> event, giving a
    /// connected debugger the chance to pause before the stack unwinds. If
    /// no handler is subscribed the call is effectively a no-op.
    /// </summary>
    internal void OnException(JsValue exception, in SourceLocation location)
    {
        if (Exception is null) return; // hot path — no debugger interested
        if (_paused) return;            // re-entrance guard (same as OnStep)
        _paused = true;
        try
        {
            CurrentLocation = location;
            Pause(PauseType.Exception, node: null, location, exception: exception, isUncaught: !IsInsideTryBlock);
        }
        finally
        {
            _paused = false;
        }
    }

    private void Pause(
        PauseType type,
        Node? node,
        in SourceLocation location,
        JsValue exception,
        bool isUncaught)
    {
        var info = new DebugInformation(
            engine: _engine,
            currentNode: node,
            currentLocation: location,
            returnValue: null,
            currentMemoryUsage: _engine.CurrentMemoryUsage,
            pauseType: type,
            breakPoint: null,
            exception: exception,
            isUncaught: isUncaught
        );

        var result = Exception?.Invoke(_engine, info);
        HandleNewStepMode(result);
    }

    private void HandleNewStepMode(StepMode? newStepMode)
    {
        if (newStepMode != null)
        {
            _steppingDepth = newStepMode switch
            {
                StepMode.Over => _engine.CallStack.Count,// Resume stepping when back at this level of the stack
                StepMode.Out => _engine.CallStack.Count - 1,// Resume stepping when we've popped the stack
                StepMode.None => int.MinValue,// Never step
                _ => int.MaxValue,// Always step
            };
        }
    }
}
