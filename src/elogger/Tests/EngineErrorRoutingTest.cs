using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Microsoft.Extensions.Logging;
using ZLogger;
using static GdUnit4.Assertions;

namespace Enaweg.Logger.Tests;

[TestSuite]
[RequireGodotRuntime]
public partial class EngineErrorRoutingTest
{
    [TestCase]
    public void EngineError_ReachesOtherSinksInTheSameFactory()
    {
        var marker = Guid.NewGuid().ToString("N");
        var sink = new MemorySink();

        using (var factory = LoggerFactory.Create(logging =>
               {
                   logging.SetMinimumLevel(LogLevel.Trace);
                   logging.AddZLoggerGodotDebug(o => o.EPluginIntegration = false);
                   logging.AddZLoggerLogProcessor(sink);
               }))
        {
            // Force the provider (and with it the OS logger) to be created.
            factory.CreateLogger("warmup");

            GD.PushError($"engine-error-{marker}");
        }

        AssertThat(sink.Messages.Any(m => m.Contains($"engine-error-{marker}"))).IsTrue();
    }

    [TestCase]
    public void EngineWarning_ReachesOtherSinksInTheSameFactory()
    {
        var marker = Guid.NewGuid().ToString("N");
        var sink = new MemorySink();

        using (var factory = LoggerFactory.Create(logging =>
               {
                   logging.SetMinimumLevel(LogLevel.Trace);
                   logging.AddZLoggerGodotDebug(o => o.EPluginIntegration = false);
                   logging.AddZLoggerLogProcessor(sink);
               }))
        {
            factory.CreateLogger("warmup");

            GD.PushWarning($"engine-warning-{marker}");
        }

        AssertThat(sink.Messages.Any(m => m.Contains($"engine-warning-{marker}"))).IsTrue();
    }

    [TestCase]
    public void ApplicationLog_IsNotEchoedBackThroughTheOsLogger()
    {
        var marker = Guid.NewGuid().ToString("N");
        var sink = new MemorySink();

        using (var factory = LoggerFactory.Create(logging =>
               {
                   logging.SetMinimumLevel(LogLevel.Trace);
                   logging.AddZLoggerGodotDebug(o => o.EPluginIntegration = false);
                   logging.AddZLoggerLogProcessor(sink);
               }))
        {
            factory.CreateLogger("app").ZLogInformation($"app-message-{marker}");
        }

        // The Godot processor prints this, which the OS logger sees again. The guard must stop it from being
        // re-logged, otherwise every application log lands in every sink twice.
        AssertThat(sink.Messages.Count(m => m.Contains($"app-message-{marker}"))).IsEqual(1);
    }

    sealed class MemorySink : IAsyncLogProcessor
    {
        readonly IZLoggerFormatter formatter = new ZLoggerOptions().CreateFormatter();
        readonly List<string> messages = new();

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (messages) return messages.ToArray();
            }
        }

        public ValueTask DisposeAsync() => default;

        public void Post(IZLoggerEntry log)
        {
            try
            {
                var writer = new ArrayBufferWriter<byte>();
                formatter.FormatLogEntry(writer, log);
                lock (messages) messages.Add(Encoding.UTF8.GetString(writer.WrittenSpan));
            }
            finally
            {
                log.Return();
            }
        }
    }
}
