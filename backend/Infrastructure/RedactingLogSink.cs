using System.Diagnostics;
using Kairon.Backend.Services.Audit;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Kairon.Backend.Infrastructure;

/// <summary>
/// The backend's log sinks. Every sink is wrapped in <see cref="RedactingLogSink"/>, so the
/// <see cref="Redaction"/> policy is applied once, centrally, to everything that reaches the console
/// or the log files - including values a call site forgot to scrub and exception text. Call-site
/// scrubbing stays as defence in depth; scrubbing is idempotent, so the overlap is harmless.
/// </summary>
public static class KaironLogging
{
    public static LoggerConfiguration WriteToRedactedSinks(this LoggerConfiguration configuration, string logsDirectory) =>
        LoggerSinkConfiguration.Wrap(configuration.WriteTo, sink => new RedactingLogSink(sink), sinks =>
        {
            sinks.Console();
            sinks.File(
                Path.Combine(logsDirectory, "kairon-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true);
        });
}

/// <summary>
/// Rewrites each event before the wrapped sinks see it: string property values (recursively, with
/// their property names, so a "token"/"secret"/"cookie" field is masked whole), the message
/// template text (an interpolated message carries its data there), and the exception's message,
/// stack trace and ToString(). Property names, levels, timestamps and trace ids are kept, so
/// structured logging is unchanged apart from the masked values.
/// </summary>
public sealed class RedactingLogSink : ILogEventSink, IDisposable
{
    private static readonly MessageTemplateParser Parser = new();
    private readonly ILogEventSink _inner;

    public RedactingLogSink(ILogEventSink inner) => _inner = inner;

    public void Emit(LogEvent logEvent) => _inner.Emit(Redact(logEvent));

    public void Dispose() => (_inner as IDisposable)?.Dispose();

    public static LogEvent Redact(LogEvent logEvent)
    {
        var template = logEvent.MessageTemplate;
        var templateText = Redaction.Scrub(template.Text) ?? template.Text;
        if (!string.Equals(templateText, template.Text, StringComparison.Ordinal))
            template = Parser.Parse(templateText);

        return new LogEvent(
            logEvent.Timestamp,
            logEvent.Level,
            logEvent.Exception is null ? null : new RedactedException(logEvent.Exception),
            template,
            logEvent.Properties.Select(p => new LogEventProperty(p.Key, Redact(p.Key, p.Value))),
            logEvent.TraceId ?? default(ActivityTraceId),
            logEvent.SpanId ?? default(ActivitySpanId));
    }

    private static LogEventPropertyValue Redact(string? name, LogEventPropertyValue value) => value switch
    {
        ScalarValue { Value: string text } => new ScalarValue(Redaction.ScrubNamed(name, text)),
        // A non-destructured object (a Uri, a request) is rendered with ToString() by the sink;
        // render it here instead so the text can be scrubbed. Primitives carry no secret text.
        ScalarValue { Value: { } other } when other is not ValueType => new ScalarValue(Redaction.ScrubNamed(name, other.ToString())),
        SequenceValue sequence => new SequenceValue(sequence.Elements.Select(e => Redact(name, e))),
        StructureValue structure => new StructureValue(
            structure.Properties.Select(p => new LogEventProperty(p.Name, Redact(p.Name, p.Value))), structure.TypeTag),
        DictionaryValue dictionary => new DictionaryValue(dictionary.Elements.Select(e =>
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(e.Key, Redact(e.Key.Value?.ToString(), e.Value)))),
        _ => value
    };

    /// <summary>Stands in for the logged exception: same type name in its text, scrubbed content.
    /// The original is never handed to a sink, so no sink can render its raw message.</summary>
    private sealed class RedactedException : Exception
    {
        private readonly string _text;
        private readonly string? _stackTrace;

        public RedactedException(Exception original)
            : base(Redaction.Scrub(original.Message))
        {
            _text = Redaction.Scrub(original.ToString()) ?? original.GetType().FullName ?? nameof(Exception);
            _stackTrace = Redaction.Scrub(original.StackTrace);
        }

        public override string? StackTrace => _stackTrace;

        public override string ToString() => _text;
    }
}
