using Jint.Runtime.Interpreter.Expressions;

namespace Jint.Runtime.Interpreter.Statements;

/// <summary>
/// http://www.ecma-international.org/ecma-262/5.1/#sec-12.13
/// </summary>
internal sealed class JintThrowStatement : JintStatement<ThrowStatement>
{
    private readonly JintExpression _argument;

    public JintThrowStatement(ThrowStatement statement) : base(statement)
    {
        _argument = JintExpression.Build(statement.Argument);
    }

    protected override Completion ExecuteInternal(EvaluationContext context)
    {
        var error = _argument.GetValue(context);

        // Notify any attached debugger BEFORE we propagate the throw — gives
        // pause-on-exception a chance to capture the live call frame + locals.
        // The check is hoisted so production engines (no Exception handler)
        // pay only a single null-comparison.
        if (context.Engine.Options.Debugger.Enabled)
        {
            context.Engine.Debugger.OnException(error, _argument._expression.Location);
        }

        return new Completion(CompletionType.Throw, error, _argument._expression);
    }
}