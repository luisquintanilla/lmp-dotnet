namespace LMP;

/// <summary>
/// Marks a <c>partial</c> method on an <see cref="LmpModule"/> subclass for
/// source-generated predictor wiring. The source generator emits a backing
/// <see cref="Predictor{TInput, TOutput}"/> field and the method body that
/// delegates to <c>PredictAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// The decorated method must be <c>partial</c>, return <c>Task&lt;TOutput&gt;</c>
/// where <c>TOutput</c> is a class, and take a single parameter of type <c>TInput</c>.
/// The containing class must be a <c>partial</c> class deriving from <see cref="LmpModule"/>.
/// </para>
/// <para>
/// The source generator creates a lazy-initialized backing <c>Predictor&lt;TInput, TOutput&gt;</c>
/// field using the module's <see cref="LmpModule.Client"/> property. This predictor is
/// automatically included in the module's <see cref="LmpModule.GetPredictors"/> result.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public partial class TicketTriageModule : LmpModule
/// {
///     public TicketTriageModule(IChatClient client) { Client = client; }
///
///     [Predict]
///     public partial Task&lt;ClassifyTicket&gt; ClassifyAsync(TicketInput input);
///
///     [Predict]
///     public partial Task&lt;DraftReply&gt; DraftAsync(ClassifyTicket classification);
///
///     public override async Task&lt;object&gt; ForwardAsync(object input, CancellationToken ct)
///     {
///         var ticket = (TicketInput)input;
///         var classification = await ClassifyAsync(ticket);
///         return await DraftAsync(classification);
///     }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class PredictAttribute : Attribute;
