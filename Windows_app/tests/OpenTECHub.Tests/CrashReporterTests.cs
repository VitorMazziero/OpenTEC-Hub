using System;
using System.IO;
using OpenTECHub.Services.Diagnostics;
using Xunit;

namespace OpenTECHub.Tests;

[Collection("AppPaths")]
public sealed class CrashReporterTests : IDisposable
{
    private readonly string _tempDir;

    public CrashReporterTests()
    {
        CrashReporter.SuppressUiForTesting = true;
        _tempDir = Path.Combine(Path.GetTempPath(), "CrashReporterTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        CrashReporter.SuppressUiForTesting = false;
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures in temp directory
        }
    }

    [Fact]
    public void BuildReportText_Includes_ExceptionDetails_And_EnvironmentInfo()
    {
        var inner = new InvalidOperationException("Inner failure detail");
        var outer = new ApplicationException("Outer failure occurred", inner);
        outer.Data["RigId"] = "BIOR-01";

        var timestampUtc = DateTimeOffset.UtcNow;
        var timestampLocal = timestampUtc.ToLocalTime();

        var report = CrashReporter.BuildReportText(
            outer,
            "UnitTestRunner",
            isTerminating: true,
            timestampUtc,
            timestampLocal);

        Assert.Contains("OPENTEC-HUB — RELATÓRIO DE PÂNICO", report);
        Assert.Contains("UnitTestRunner", report);
        Assert.Contains("SIM (Falha Irrecuperável)", report);
        Assert.Contains("ApplicationException", report);
        Assert.Contains("Outer failure occurred", report);
        Assert.Contains("Inner failure detail", report);
        Assert.Contains("RigId", report);
        Assert.Contains("BIOR-01", report);
        Assert.Contains("Sistema Operacional:", report);
        Assert.Contains("Runtime .NET:", report);
    }

    [Fact]
    public void BuildReportText_Handles_AggregateException_WithMultipleInners()
    {
        var ex1 = new ArgumentException("Invalid argument 1");
        var ex2 = new TimeoutException("Timed out waiting for ACK");
        var aggregate = new AggregateException("Multiple faults detected", ex1, ex2);

        var report = CrashReporter.BuildReportText(
            aggregate,
            "BackgroundWorker",
            isTerminating: false,
            DateTimeOffset.UtcNow,
            DateTimeOffset.Now);

        Assert.Contains("NÃO (Tratado no Dispatcher/Task)", report);
        Assert.Contains("Invalid argument 1", report);
        Assert.Contains("Timed out waiting for ACK", report);
        Assert.Contains("Exceção Agregada #1", report);
        Assert.Contains("Exceção Agregada #2", report);
    }

    [Fact]
    public void GenerateAndSaveReport_Writes_File_To_PreferredDirectory()
    {
        var testException = new InvalidOperationException("Test fault for disk persistence");

        var path = CrashReporter.GenerateAndSaveReport(
            testException,
            "DiskPersistenceTest",
            isTerminating: false,
            preferredDirectory: _tempDir);

        Assert.True(File.Exists(path), $"Crash report file should exist at: {path}");
        var content = File.ReadAllText(path);
        Assert.Contains("Test fault for disk persistence", content);
        Assert.Contains("DiskPersistenceTest", content);
    }

    [Fact]
    public void CrashReporter_Instance_Implements_Interface_And_WritesReport()
    {
        ICrashReporter reporter = new CrashReporter();
        var ex = new DivideByZeroException("Attempted division by zero");

        var path = reporter.WriteCrashReport(ex, "InterfaceTest", isTerminating: false);

        try
        {
            Assert.False(string.IsNullOrWhiteSpace(path));
            Assert.True(File.Exists(path));
            var content = File.ReadAllText(path);
            Assert.Contains("DivideByZeroException", content);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                try { File.Delete(path); } catch { }
            }
        }
    }

    [Fact]
    public void IsShutdownCrtUnloadException_Identifies_SingletonDomainUnload_DllNotFound()
    {
        var stackTrace = "   at __std_type_info_destroy_list(__type_info_node*)\r\n" +
                         "   at __scrt_uninitialize_type_info()\r\n" +
                         "   at _app_exit_callback()\r\n" +
                         "   at <CrtImplementationDetails>.ModuleUninitializer.SingletonDomainUnload(Object source, EventArgs arguments)";

        var ex = new CustomStackTraceDllNotFoundException("Dll was not found.", stackTrace);

        Assert.True(CrashReporter.IsShutdownCrtUnloadException(ex));

        // Normal DllNotFoundException without CRT unload stack trace should return false
        var normalEx = new DllNotFoundException("Missing SomeLibrary.dll");
        Assert.False(CrashReporter.IsShutdownCrtUnloadException(normalEx));

        // Other exception types should return false
        var invalidOp = new InvalidOperationException("Something failed");
        Assert.False(CrashReporter.IsShutdownCrtUnloadException(invalidOp));
    }

    [Fact]
    public void GenerateAndSaveReport_Suppresses_ShutdownCrtUnloadException()
    {
        var stackTrace = "   at <CrtImplementationDetails>.ModuleUninitializer.SingletonDomainUnload(Object source, EventArgs arguments)";
        var ex = new CustomStackTraceDllNotFoundException("Dll was not found.", stackTrace);

        var result = CrashReporter.GenerateAndSaveReport(ex, "AppDomain.CurrentDomain.UnhandledException", isTerminating: true, preferredDirectory: _tempDir);

        Assert.Contains("Suprimido", result);
        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    private sealed class CustomStackTraceDllNotFoundException : DllNotFoundException
    {
        private readonly string _customStackTrace;

        public CustomStackTraceDllNotFoundException(string message, string stackTrace) : base(message)
        {
            _customStackTrace = stackTrace;
        }

        public override string StackTrace => _customStackTrace;
    }
}
