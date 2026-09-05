using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using OpenTECHub.Services.Persistence;
using Serilog;

namespace OpenTECHub.Services.Diagnostics;

/// <summary>
/// Contrato do serviço de diagnóstico e captura de falhas críticas (Crash Reporting).
/// </summary>
public interface ICrashReporter
{
    string WriteCrashReport(Exception exception, string source, bool isTerminating);
}

/// <summary>
/// Serviço de captura de exceções não tratadas e geração de relatórios de pânico para auditoria em campo.
/// Garante que qualquer falha grave grave contexto detalhado do ambiente, memória e rastreamento de pilha
/// antes da finalização do processo.
/// </summary>
public sealed class CrashReporter : ICrashReporter
{
    private const string FallbackFolderName = "OpenTEC-Hub";
    private const string CrashSubFolderName = "CrashDumps";

    /// <summary>
    /// Permite que testes unitários desabilitem a exibição de caixas de diálogo durante a validação.
    /// </summary>
    public static bool SuppressUiForTesting { get; set; }

    /// <summary>
    /// Caminho padrão do diretório de contingência local (%LOCALAPPDATA%\OpenTEC-Hub\CrashDumps).
    /// </summary>
    public static string FallbackCrashDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            FallbackFolderName,
            CrashSubFolderName);

    /// <inheritdoc />
    public string WriteCrashReport(Exception exception, string source, bool isTerminating)
    {
        return GenerateAndSaveReport(exception, source, isTerminating);
    }

    /// <summary>
    /// Gera e persiste o relatório de pânico em disco, registrando em log e exibindo diálogo ao operador.
    /// </summary>
    public static string GenerateAndSaveReport(
        Exception exception,
        string source,
        bool isTerminating,
        string? preferredDirectory = null)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var localTimestamp = timestamp.ToLocalTime();
        var fileId = localTimestamp.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..6];
        var fileName = $"crash_{fileId}.log";

        var reportContent = BuildReportText(exception, source, isTerminating, timestamp, localTimestamp);

        // 1. Tentar gravar no diretório preferencial ou no Workspace/Logs/Crash
        string? primaryPath = null;
        try
        {
            var targetDir = preferredDirectory;
            if (string.IsNullOrWhiteSpace(targetDir))
            {
                var baseLogDir = AppPaths.LogDirectory;
                targetDir = Path.Combine(baseLogDir, "Crash");
            }

            Directory.CreateDirectory(targetDir);
            primaryPath = Path.Combine(targetDir, fileName);
            File.WriteAllText(primaryPath, reportContent, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao gravar relatório de pânico no diretório primário do workspace");
        }

        // 2. Gravar cópia de contingência no LocalAppData (garante persistência se o workspace falhar)
        string? fallbackPath = null;
        try
        {
            var fallbackDir = FallbackCrashDirectory;
            Directory.CreateDirectory(fallbackDir);
            fallbackPath = Path.Combine(fallbackDir, fileName);
            File.WriteAllText(fallbackPath, reportContent, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao gravar relatório de contingência no LocalAppData");
        }

        var savedPath = primaryPath ?? fallbackPath ?? "Falha de gravação em disco";

        // Registrar também no Serilog caso o logger ainda esteja operacional
        Log.Fatal(exception, "CRASH REPORT GERADO [{SavedPath}] - Origem: {Source}, Terminando: {IsTerminating}", savedPath, source, isTerminating);
        Log.CloseAndFlush();

        // 3. Notificar operador via MessageBox se houver sessão de UI interativa
        if (!SuppressUiForTesting)
        {
            ShowCrashDialog(exception, savedPath, isTerminating);
        }

        return savedPath;
    }

    /// <summary>
    /// Constrói o texto do relatório de pânico formatado com dados de ambiente e exceção.
    /// </summary>
    public static string BuildReportText(
        Exception exception,
        string source,
        bool isTerminating,
        DateTimeOffset timestampUtc,
        DateTimeOffset timestampLocal)
    {
        var sb = new StringBuilder(2048);
        var appVersion = GetApplicationVersion();
        var currentProcess = GetCurrentProcessSafe();

        sb.AppendLine("================================================================================");
        sb.AppendLine("               OPENTEC-HUB — RELATÓRIO DE PÂNICO / CRASH REPORT                 ");
        sb.AppendLine("================================================================================");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Data/Hora (Local):    {timestampLocal:yyyy-MM-dd HH:mm:ss.fff zzz}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Data/Hora (UTC):      {timestampUtc:yyyy-MM-dd HH:mm:ss.fff}Z");
        sb.AppendLine($"Origem da Falha:      {source}");
        sb.AppendLine($"Processo Terminando:  {(isTerminating ? "SIM (Falha Irrecuperável)" : "NÃO (Tratado no Dispatcher/Task)")}");
        sb.AppendLine($"Versão do OpenTEC:    {appVersion}");
        sb.AppendLine();

        sb.AppendLine("── INFORMAÇÕES DO AMBIENTE E SISTEMA OPERACIONAL ─────────────────────────────");
        sb.AppendLine($"Sistema Operacional:  {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Arquitetura SO:       {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"Arquitetura Processo: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"Runtime .NET:         {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Processadores:        {Environment.ProcessorCount} núcleos lógicos");
        sb.AppendLine($"Nome da Máquina:      {Environment.MachineName}");
        sb.AppendLine($"Usuário do Sistema:   {Environment.UserName}");
        sb.AppendLine();

        sb.AppendLine("── MÉTRICAS DO PROCESSO ───────────────────────────────────────────────────────");
        if (currentProcess is not null)
        {
            try
            {
                var uptime = DateTime.UtcNow - currentProcess.StartTime.ToUniversalTime();
                var workingSetMb = currentProcess.WorkingSet64 / (1024.0 * 1024.0);
                var privateMb = currentProcess.PrivateMemorySize64 / (1024.0 * 1024.0);
                var gcMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

                sb.AppendLine(CultureInfo.InvariantCulture, $"Process ID:           {currentProcess.Id}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Tempo de Execução:    {uptime.TotalHours:F0}h {uptime.Minutes}m {uptime.Seconds}s");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Memória WorkingSet:   {workingSetMb:F2} MB");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Memória Privada:      {privateMb:F2} MB");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Memória Gerenciada:   {gcMb:F2} MB (GC Heap)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Contagem de Threads:  {currentProcess.Threads.Count}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Falha ao obter detalhes do processo: {ex.Message}");
            }
        }
        else
        {
            sb.AppendLine("Detalhes do processo não disponíveis.");
        }

        try
        {
            sb.AppendLine($"Workspace Configurado: {AppPaths.DataDirectory}");
        }
        catch
        {
            sb.AppendLine("Workspace Configurado: [Indisponível / Não Inicializado]");
        }
        sb.AppendLine();

        sb.AppendLine("── DETALHAMENTO DA EXCEÇÃO ───────────────────────────────────────────────────");
        AppendExceptionDetails(sb, exception, level: 0);

        sb.AppendLine("================================================================================");
        sb.AppendLine("Fim do Relatório de Pânico.");
        return sb.ToString();
    }

    private static void AppendExceptionDetails(StringBuilder sb, Exception ex, int level)
    {
        var indent = new string(' ', level * 2);
        var prefix = level == 0 ? "[Exceção Principal]" : $"[Inner Exception #{level}]";

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{prefix} Tipo: {ex.GetType().FullName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Mensagem: {ex.Message}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}HResult: 0x{ex.HResult:X8}");

        if (ex.Data.Count > 0)
        {
            sb.AppendLine($"{indent}Dados Adicionais (Exception.Data):");
            foreach (var key in ex.Data.Keys)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}  - {key}: {ex.Data[key]}");
            }
        }

        sb.AppendLine($"{indent}Rastreamento de Pilha (StackTrace):");
        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            var lines = ex.StackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}  {line.Trim()}");
            }
        }
        else
        {
            sb.AppendLine($"{indent}  [Rastreamento de pilha não disponível]");
        }
        sb.AppendLine();

        if (ex is AggregateException agg)
        {
            var index = 1;
            foreach (var inner in agg.InnerExceptions)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}--- Exceção Agregada #{index++} ---");
                AppendExceptionDetails(sb, inner, level + 1);
            }
        }
        else if (ex.InnerException is not null)
        {
            AppendExceptionDetails(sb, ex.InnerException, level + 1);
        }
    }

    private static void ShowCrashDialog(Exception ex, string savedPath, bool isTerminating)
    {
        try
        {
            var title = isTerminating ? "OpenTEC-Hub — Falha Crítica" : "OpenTEC-Hub — Erro Inesperado";
            var message = isTerminating
                ? $"Ocorreu uma falha irrecuperável e o OpenTEC-Hub precisará ser encerrado.\n\n" +
                  $"Exceção: {ex.GetType().Name}\n" +
                  $"Mensagem: {ex.Message}\n\n" +
                  $"Um relatório detalhado de pânico foi salvo em:\n{savedPath}\n\n" +
                  $"Por favor, envie este arquivo para a equipe técnica de suporte."
                : $"Ocorreu um erro inesperado durante a execução.\n\n" +
                  $"Exceção: {ex.GetType().Name}\n" +
                  $"Mensagem: {ex.Message}\n\n" +
                  $"O relatório de diagnóstico foi salvo em:\n{savedPath}";

            MessageBox.Show(
                message,
                title,
                MessageBoxButton.OK,
                isTerminating ? MessageBoxImage.Error : MessageBoxImage.Warning);
        }
        catch
        {
            // Silencioso se o MessageBox não puder ser exibido (ex.: sessão não interativa)
        }
    }

    private static string GetApplicationVersion()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(CrashReporter).Assembly;
            var info = FileVersionInfo.GetVersionInfo(assembly.Location);
            return info.ProductVersion ?? info.FileVersion ?? "0.24.0";
        }
        catch
        {
            return "0.24.0";
        }
    }

    private static Process? GetCurrentProcessSafe()
    {
        try
        {
            return Process.GetCurrentProcess();
        }
        catch
        {
            return null;
        }
    }
}
