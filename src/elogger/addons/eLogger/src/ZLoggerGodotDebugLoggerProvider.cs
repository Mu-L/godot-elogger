using System;
using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal;
using Godot;
using Godot.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZLogger;
using Environment = System.Environment;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Enaweg.Logger;

public sealed class ZLoggerGodotDebugOptions : ZLoggerOptions
{
    public bool PrettyStacktrace { get; set; } = true;
    public bool EPluginIntegration { get; set; } = true;
}

public static class ZLoggerGodotExtensions
{
    public static ILoggingBuilder AddZLoggerGodotDebug(this ILoggingBuilder builder) =>
        builder.AddZLoggerGodotDebug(_ => { });

    public static ILoggingBuilder AddZLoggerGodotDebug(this ILoggingBuilder builder,
        Action<ZLoggerGodotDebugOptions> configure)
    {
        builder.Services.AddSingleton<ILoggerProvider, ZLoggerGodotDebugLoggerProvider>(serviceProvider =>
        {
            var options = new ZLoggerGodotDebugOptions();
            configure(options);
            return new ZLoggerGodotDebugLoggerProvider(options, serviceProvider);
        });
        return builder;
    }
}

public class GodotDebugLogProcessor : IAsyncLogProcessor
{
    [ThreadStatic] static ArrayBufferWriter<byte>? bufferWriter;

    readonly ZLoggerGodotDebugOptions options;
    readonly IZLoggerFormatter formatter;

    public GodotDebugLogProcessor(ZLoggerGodotDebugOptions options)
    {
        this.options = options;
        formatter = options.CreateFormatter();
    }

    public ValueTask DisposeAsync()
    {
        return default;
    }

    public void Post(IZLoggerEntry log)
    {
        try
        {
            var context = log.LogInfo.Context as GodotObject;
            var msg = FormatToString(log, formatter);

            if (log.LogInfo.Exception is not null && options.PrettyStacktrace)
            {
                var stacktrace = new StackTrace(log.LogInfo.Exception, true);
                msg =
                    $"{msg}{Environment.NewLine}{DiagnosticsHelper.CleanupStackTrace(stacktrace)}{Environment.NewLine}---";
            }

            if (context is not null)
            {
                msg = $"(#{context.GetInstanceId()}) {msg}";
            }

            using var _ = GodotLogGuard.Enter();
            switch (log.LogInfo.LogLevel)
            {
                case LogLevel.Error or LogLevel.Critical:
                    GD.PushError(msg);
                    break;
                case LogLevel.Warning:
                    GD.PushWarning(msg);
                    break;
                default:
                    GD.Print(msg);
                    break;
            }
        }
        finally
        {
            log.Return();
        }
    }

    static string FormatToString(IZLoggerEntry entry, IZLoggerFormatter formatter)
    {
        bufferWriter ??= new ArrayBufferWriter<byte>();
        bufferWriter.Clear();

        formatter.FormatLogEntry(bufferWriter, entry);
        return Encoding.UTF8.GetString(bufferWriter.WrittenSpan);
    }
}

internal sealed partial class GodotOSLogger : Godot.Logger
{
    readonly Func<ILogger> loggerAccessor;

    public GodotOSLogger(ILogger logger) : this(() => logger)
    {
    }

    public GodotOSLogger(Func<ILogger> loggerAccessor)
    {
        this.loggerAccessor = loggerAccessor;
    }

    ILogger Logger => loggerAccessor();

    public override void _LogError(string function, string file, int line, string code, string rationale,
        bool editorNotify, int errorType, Array<ScriptBacktrace> scriptBacktraces)
    {
        base._LogError(function, file, line, code, rationale, editorNotify, errorType, scriptBacktraces);
        if (GodotLogGuard.IsWriting)
        {
            return;
        }

        // Godot puts the failing expression in "code" and the optional explanatory message in "rationale".
        // GD.PushError/GD.PushWarning and the ERR_*_MSG macros leave "rationale" empty, so preferring it
        // unconditionally logged blank entries. Mirror the engine's own Logger::log_error and fall back to "code".
        var details = string.IsNullOrEmpty(rationale) ? code : rationale;

        switch (errorType)
        {
            case (int)ErrorType.Error or (int)ErrorType.Script or (int)ErrorType.Shader:
                Logger.ZLogError($"{details}", null, function, file, line);
                break;
            case (int)ErrorType.Warning:
                Logger.ZLogWarning($"{details}", null, function, file, line);
                break;
        }
    }

    public override void _LogMessage(string message, bool error)
    {
        base._LogMessage(message, error);
        if (GodotLogGuard.IsWriting)
        {
            return;
        }

        if (error)
        {
            Logger.ZLogError($"{message}");
        }
        else
        {
            Logger.ZLogInformation($"{message}");
        }
    }
}

[ProviderAlias("ZLoggerGodotDebug")]
public class ZLoggerGodotDebugLoggerProvider : ILoggerProvider, ISupportExternalScope, IAsyncDisposable
{
    const string EngineLoggerCategory = "OSLogger";

    readonly ZLoggerOptions options;
    readonly GodotDebugLogProcessor processor;
    readonly GodotOSLogger godotLogger;
    IExternalScopeProvider? scopeProvider;
    int isDisposed;

    public ZLoggerGodotDebugLoggerProvider(ZLoggerGodotDebugOptions options) : this(options, null)
    {
    }

    public ZLoggerGodotDebugLoggerProvider(ZLoggerGodotDebugOptions options, IServiceProvider? serviceProvider)
    {
        this.options = options;
        this.processor = new GodotDebugLogProcessor(options);

        godotLogger = new GodotOSLogger(CreateEngineLoggerAccessor(serviceProvider));
        OS.AddLogger(godotLogger);

        if (options.EPluginIntegration)
        {
#if TOOLS
            // ePlugin only exists while the editor is running its plugins. In a game build (or when the game is run
            // from the editor, which still compiles with TOOLS defined) EGlobal has no plugin context, so switching
            // its logging is not possible and must be skipped instead of throwing.
            if (EGlobal.Instance.IsValid())
            {
                EGlobal.Instance.SwitchLogging(new EPluginLoggerFactory(this));
            }
#endif
        }
    }

    /// <summary>
    /// Builds the logger that intercepted engine errors are written to.
    /// <para>
    /// Engine errors belong in every sink the application configured, not just this provider's, so the logger is
    /// taken from the application's <see cref="ILoggerFactory" /> when one is reachable. That resolution has to
    /// happen lazily: the factory depends on every <see cref="ILoggerProvider" />, so it cannot be resolved while
    /// this provider is still being constructed. When the provider is built by hand there is no service provider
    /// and it falls back to logging through itself.
    /// </para>
    /// </summary>
    Func<ILogger> CreateEngineLoggerAccessor(IServiceProvider? serviceProvider)
    {
        if (serviceProvider is null)
        {
            return () => CreateLogger(EngineLoggerCategory);
        }

        ILogger? resolved = null;
        return () =>
        {
            if (resolved is not null)
            {
                return resolved;
            }

            try
            {
                return resolved = serviceProvider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(EngineLoggerCategory);
            }
            catch (Exception)
            {
                // An engine error can arrive before the factory is ready. Capturing it must never fail, so fall
                // back to this provider without caching - the next error retries the full factory.
                return CreateLogger(EngineLoggerCategory);
            }
        };
    }

    public ILogger CreateLogger(string categoryName)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref isDisposed) != 0, this);

        return new ZLoggerLogger(categoryName, processor, options, options.IncludeScopes ? scopeProvider : null);
    }

    public void Dispose()
    {
        if (!TryBeginDispose())
        {
            return;
        }

        try
        {
            RemoveGodotLogger();
        }
        finally
        {
            processor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!TryBeginDispose())
        {
            return;
        }

        try
        {
            RemoveGodotLogger();
        }
        finally
        {
            await processor.DisposeAsync().ConfigureAwait(false);
        }
    }

    bool TryBeginDispose()
    {
        return Interlocked.Exchange(ref isDisposed, 1) == 0;
    }

    void RemoveGodotLogger()
    {
        try
        {
            OS.RemoveLogger(godotLogger);
        }
        finally
        {
            godotLogger.Dispose();
        }
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        this.scopeProvider = scopeProvider;
    }
}
