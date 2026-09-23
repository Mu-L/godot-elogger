using System;
using System.Collections.Generic;
using GdUnit4;
using Godot;
using Godot.Collections;
using ErrorType = Godot.Logger.ErrorType;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static GdUnit4.Assertions;

namespace Enaweg.Logger.Tests;

[TestSuite]
[RequireGodotRuntime]
public partial class GodotOSLoggerTest
{
    [TestCase]
    public void LogError_WithEmptyRationale_FallsBackToCode()
    {
        var recorder = new RecordingLogger();
        var sut = new GodotOSLogger(recorder);

        // GD.PushError and the ERR_* macros put the message in "code" and leave "rationale" empty.
        LogError(sut, code: "something went wrong", rationale: "", errorType: ErrorType.Error);

        AssertThat(recorder.Entries).HasSize(1);
        AssertThat(recorder.Entries[0].Level).IsEqual(LogLevel.Error);
        AssertThat(recorder.Entries[0].Message).IsEqual("something went wrong");
    }

    [TestCase]
    public void LogError_WithRationale_PrefersRationale()
    {
        var recorder = new RecordingLogger();
        var sut = new GodotOSLogger(recorder);

        LogError(sut, code: "Condition \"x\" is true.", rationale: "x must not be set", errorType: ErrorType.Error);

        AssertThat(recorder.Entries).HasSize(1);
        AssertThat(recorder.Entries[0].Message).IsEqual("x must not be set");
    }

    [TestCase]
    public void LogError_WithWarningType_LogsAtWarningLevel()
    {
        var recorder = new RecordingLogger();
        var sut = new GodotOSLogger(recorder);

        LogError(sut, code: "heads up", rationale: "", errorType: ErrorType.Warning);

        AssertThat(recorder.Entries).HasSize(1);
        AssertThat(recorder.Entries[0].Level).IsEqual(LogLevel.Warning);
        AssertThat(recorder.Entries[0].Message).IsEqual("heads up");
    }

    [TestCase]
    public void LogError_ForScriptAndShaderTypes_LogsAtErrorLevel()
    {
        var recorder = new RecordingLogger();
        var sut = new GodotOSLogger(recorder);

        LogError(sut, code: "script boom", rationale: "", errorType: ErrorType.Script);
        LogError(sut, code: "shader boom", rationale: "", errorType: ErrorType.Shader);

        AssertThat(recorder.Entries).HasSize(2);
        AssertThat(recorder.Entries[0].Level).IsEqual(LogLevel.Error);
        AssertThat(recorder.Entries[0].Message).IsEqual("script boom");
        AssertThat(recorder.Entries[1].Level).IsEqual(LogLevel.Error);
        AssertThat(recorder.Entries[1].Message).IsEqual("shader boom");
    }

    [TestCase]
    public void LogMessage_WhileAProcessorIsWritingToGodot_IsIgnored()
    {
        var recorder = new RecordingLogger();
        var sut = new GodotOSLogger(recorder);

        using (GodotLogGuard.Enter())
        {
            sut._LogMessage("echo of our own output", false);
        }

        AssertThat(recorder.Entries).IsEmpty();
    }

    [TestCase]
    public void CreateLogger_AfterDispose_Throws()
    {
        var provider = new ZLoggerGodotDebugLoggerProvider(
            new ZLoggerGodotDebugOptions { EPluginIntegration = false });
        provider.Dispose();

        AssertThrown(() => provider.CreateLogger("after-dispose"))
            .IsInstanceOf<ObjectDisposedException>();
    }

    [TestCase]
    public void CreateLogger_BeforeDispose_Succeeds()
    {
        using var provider = new ZLoggerGodotDebugLoggerProvider(
            new ZLoggerGodotDebugOptions { EPluginIntegration = false });

        AssertThat(provider.CreateLogger("before-dispose")).IsNotNull();
    }

    [TestCase]
    public void EngineLoggerAccessor_WithoutServiceProvider_ReusesOneLogger()
    {
        using var provider = new ZLoggerGodotDebugLoggerProvider(
            new ZLoggerGodotDebugOptions { EPluginIntegration = false });

        var accessor = provider.CreateEngineLoggerAccessor(null);

        // The accessor runs per engine message; it must not allocate a logger each time.
        AssertBool(ReferenceEquals(accessor(), accessor())).IsTrue();
    }

    [TestCase]
    public void EngineLoggerAccessor_AfterDispose_DoesNotThrowIntoTheEngine()
    {
        var provider = new ZLoggerGodotDebugLoggerProvider(
            new ZLoggerGodotDebugOptions { EPluginIntegration = false });
        var accessor = provider.CreateEngineLoggerAccessor(null);
        provider.Dispose();

        // An engine error racing with disposal must not push ObjectDisposedException into a Godot callback.
        // This throws, and so fails the test, if the accessor is not disposal-safe.
        AssertThat(accessor()).IsNotNull();
    }

    [TestCase]
    public void EngineLogger_AfterDispose_SwallowsMessagesInsteadOfThrowing()
    {
        var provider = new ZLoggerGodotDebugLoggerProvider(
            new ZLoggerGodotDebugOptions { EPluginIntegration = false });
        var sut = new GodotOSLogger(provider.CreateEngineLoggerAccessor(null));
        provider.Dispose();

        // Throws out of the Godot callback, and so fails the test, if the fallback is not disposal-safe.
        LogError(sut, code: "after dispose", rationale: "", errorType: ErrorType.Error);

        AssertBool(ReferenceEquals(provider.CreateEngineLoggerAccessor(null)(), NullLogger.Instance)).IsTrue();
    }

    static void LogError(GodotOSLogger sut, string code, string rationale, ErrorType errorType) =>
        sut._LogError("SomeFunction", "res://some_file.cs", 42, code, rationale, false, (int)errorType,
            new Array<ScriptBacktrace>());

    sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
