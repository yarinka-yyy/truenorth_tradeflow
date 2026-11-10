using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrueNorth.TradeFlow;

internal sealed record AiAnalysisRequest(
    Guid Id,
    string Asset,
    string Market,
    string OrderType,
    string TemplateVersion,
    string Prompt)
{
    internal string EntryType => OrderType switch
    {
        "MARKET" => "Market",
        "LIMIT" => "Limit",
        _ => OrderType
    };
}

internal static class AiAnalysisRequestFactory
{
    internal const string TemplateVersion = "ai-setup-v1";
    private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n'];

    internal static bool TryCreateBatch(
        string? assetInput,
        string? orderType,
        out IReadOnlyList<AiAnalysisRequest> requests,
        out string error)
    {
        requests = [];
        error = string.Empty;
        if (orderType is not ("Market" or "Limit"))
        {
            error = "Выберите тип входа Market или Limit.";
            return false;
        }

        var assets = (assetInput ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (assets.Length == 0)
        {
            error = "Введите хотя бы один тикер, например BTC или ETH.";
            return false;
        }

        var normalized = new List<string>(assets.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in assets)
        {
            var asset = input.ToUpperInvariant();
            if (asset.Length is < 1 or > 15 || asset.Any(character => !char.IsAsciiLetterOrDigit(character)))
            {
                error = $"Актив «{input}» не похож на тикер. Используйте только буквы A-Z и цифры; рынок будет иметь вид ASSET-USDC.";
                return false;
            }
            if (!seen.Add(asset))
            {
                error = $"Актив {asset} указан несколько раз.";
                return false;
            }
            normalized.Add(asset);
        }

        var responseOrderType = orderType.ToUpperInvariant();
        requests = normalized.Select(asset =>
        {
            var market = $"{asset}-USDC";
            var prompt = BuildPrompt(market, responseOrderType);
            return new AiAnalysisRequest(Guid.NewGuid(), asset, market, responseOrderType, TemplateVersion, prompt);
        }).ToArray();
        return true;
    }

    private static string BuildPrompt(string market, string orderType)
    {
        var entryContract = orderType == "MARKET"
            ? "Use reference_price only as the analytical anchor. Set entry_price to JSON null because no actual fill is known. Do not output or infer a fill price."
            : "Set entry_price to one exact proposed Limit level.";
        return $$"""
            Analyze the current {{market}} perpetual market for one {{orderType}} entry using current market data. This is analysis only and is never a trade decision. {{entryContract}}

            Do not prepare, submit, change, or cancel orders. Do not access a wallet, account, agent, signatures, or trading tools. Do not include account, wallet, login, key, or other private data.

            Return exactly one JSON object with exactly these keys and types:
            {"status":"SETUP or NO_SETUP","market":"{{market}}","order_type":"{{orderType}}","side":"LONG or SHORT or null","reference_price":number or null,"reference_time_utc":"ISO 8601 UTC timestamp or null","entry_price":number or null,"take_profit":number or null,"stop_loss":number or null,"reason":"brief explanation","invalidation":"brief condition or null"}

            Use actual JSON null for unavailable values. All prices are USDC per asset. For SETUP, provide a positive reference_price, a side, positive take_profit and stop_loss, a reason, and an invalidation condition. For Limit, entry_price must be positive. For Market, entry_price must be null; use reference_price as the analytical anchor. For LONG require stop_loss < anchor < take_profit. For SHORT require take_profit < anchor < stop_loss. Return NO_SETUP when a complete format/data candidate cannot be supplied; set side, entry_price, take_profit, stop_loss, and invalidation to null. Never invent market data, a timestamp, or a fill price.
            """;
    }
}

internal sealed record AiSourceResponse(Guid ResponseRequestId, string RawText, bool IsFinalStream);

internal interface IAiAnalysisSource
{
    Task<AiSourceResponse> AnalyzeAsync(AiAnalysisRequest request, CancellationToken cancellationToken);
}

internal sealed class SyntheticAiAnalysisSource : IAiAnalysisSource
{
    public async Task<AiSourceResponse> AnalyzeAsync(AiAnalysisRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        var raw = HasSetup(request.Asset)
            ? SetupResponse(request)
            : NoSetupResponse(request);
        return new AiSourceResponse(request.Id, raw, IsFinalStream: true);
    }

    private static bool HasSetup(string asset) => asset switch
    {
        "BTC" or "SOL" => true,
        "ETH" => false,
        _ => asset.Aggregate(0, (sum, character) => sum + character) % 2 == 0
    };

    private static string SetupResponse(AiAnalysisRequest request) => JsonSerializer.Serialize(new
    {
        status = "SETUP",
        market = request.Market,
        order_type = request.OrderType,
        side = "LONG",
        reference_price = 100m,
        reference_time_utc = (string?)null,
        entry_price = request.OrderType == "MARKET" ? (decimal?)null : 99m,
        take_profit = 110m,
        stop_loss = 95m,
        reason = "Синтетический ответ для проверки формата; рыночные данные не запрашивались.",
        invalidation = "Проверочный сценарий завершён при нарушении синтетического уровня."
    });

    private static string NoSetupResponse(AiAnalysisRequest request) => JsonSerializer.Serialize(new
    {
        status = "NO_SETUP",
        market = request.Market,
        order_type = request.OrderType,
        side = (string?)null,
        reference_price = (decimal?)null,
        reference_time_utc = (string?)null,
        entry_price = (decimal?)null,
        take_profit = (decimal?)null,
        stop_loss = (decimal?)null,
        reason = "Синтетический ответ без сетапа; данные рынка не запрашивались.",
        invalidation = (string?)null
    });
}

internal enum AiTestOutcome
{
    CompleteSetup,
    ValidNoSetup,
    IncompleteOrInvalid,
    Inconclusive,
    RequestError,
    Timeout,
    Cancelled,
    NotStarted
}

internal enum AiFreshnessState
{
    Unknown,
    TimestampPresentNotAssessed
}

internal sealed record AiParsedFields(
    string? Status,
    string? Market,
    string? OrderType,
    string? Side,
    decimal? ReferencePrice,
    string? ReferenceTimeUtc,
    decimal? EntryPrice,
    decimal? TakeProfit,
    decimal? StopLoss,
    string? Reason,
    string? Invalidation);

internal sealed record AiTestResult(
    AiAnalysisRequest Request,
    AiTestOutcome Outcome,
    DateTimeOffset? RequestStartedUtc,
    DateTimeOffset? ResponseReceivedUtc,
    string? RawFinalAnswer,
    AiParsedFields? Fields,
    AiFreshnessState Freshness,
    IReadOnlyList<string> Reasons);

internal sealed record AiTestBatchReport(
    Guid Id,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    IReadOnlyList<AiTestResult> Results)
{
    internal int Count(AiTestOutcome outcome) => Results.Count(result => result.Outcome == outcome);
    internal int ProcessedCount => Results.Count(result => result.Outcome != AiTestOutcome.NotStarted);
    internal double ProgressFraction => Results.Count == 0 ? 0 : (double)ProcessedCount / Results.Count;
    internal bool WasCancelled => Count(AiTestOutcome.Cancelled) > 0 || Count(AiTestOutcome.NotStarted) > 0;
}

internal sealed record AiTestProgress(int CompletedRequests, int TotalRequests, string? CurrentAsset, AiTestOutcome? LastOutcome);

internal static class AiResponseAssessor
{
    private static readonly HashSet<string> ExpectedKeys = new(StringComparer.Ordinal)
    {
        "status", "market", "order_type", "side", "reference_price", "reference_time_utc",
        "entry_price", "take_profit", "stop_loss", "reason", "invalidation"
    };

    internal static (AiTestOutcome Outcome, AiParsedFields? Fields, AiFreshnessState Freshness, IReadOnlyList<string> Reasons)
        Assess(AiAnalysisRequest request, AiSourceResponse response)
    {
        if (response.ResponseRequestId != request.Id)
            return (AiTestOutcome.Inconclusive, null, AiFreshnessState.Unknown,
                ["Ответ относится к другому запросу; идентичность результата не подтверждена."]);
        if (!response.IsFinalStream)
            return (AiTestOutcome.Inconclusive, null, AiFreshnessState.Unknown,
                ["Поток ответа не помечен завершённым; результат не оценивается."]);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(response.RawText, new JsonDocumentOptions { MaxDepth = 12 });
        }
        catch (JsonException)
        {
            return (AiTestOutcome.IncompleteOrInvalid, null, AiFreshnessState.Unknown,
                ["Ответ не содержит ровно один полный JSON-объект."]);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (AiTestOutcome.IncompleteOrInvalid, null, AiFreshnessState.Unknown,
                    ["Корень ответа должен быть JSON-объектом."]);

            var reasons = new List<string>();
            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!ExpectedKeys.Contains(property.Name))
                    reasons.Add($"Неизвестное поле: {property.Name}.");
                if (!properties.TryAdd(property.Name, property.Value))
                    reasons.Add($"Поле {property.Name} повторяется.");
            }

            string? ReadText(string name, bool nullable)
            {
                if (!properties.TryGetValue(name, out var value))
                {
                    reasons.Add($"Не указано обязательное поле {name}.");
                    return null;
                }
                if (value.ValueKind == JsonValueKind.Null && nullable)
                    return null;
                if (value.ValueKind != JsonValueKind.String)
                {
                    reasons.Add($"Поле {name} должно быть строкой{(nullable ? " или null" : string.Empty)}.");
                    return null;
                }
                var text = value.GetString();
                if (string.IsNullOrWhiteSpace(text))
                    reasons.Add($"Поле {name} не может быть пустым.");
                return text;
            }

            decimal? ReadPrice(string name)
            {
                if (!properties.TryGetValue(name, out var value))
                {
                    reasons.Add($"Не указано обязательное поле {name}.");
                    return null;
                }
                if (value.ValueKind == JsonValueKind.Null)
                    return null;
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
                {
                    reasons.Add($"Поле {name} должно быть конечным числом или null.");
                    return null;
                }
                return number;
            }

            var status = ReadText("status", nullable: false);
            var market = ReadText("market", nullable: false);
            var orderType = ReadText("order_type", nullable: false);
            var side = ReadText("side", nullable: true);
            var referencePrice = ReadPrice("reference_price");
            var referenceTime = ReadText("reference_time_utc", nullable: true);
            var entryPrice = ReadPrice("entry_price");
            var takeProfit = ReadPrice("take_profit");
            var stopLoss = ReadPrice("stop_loss");
            var reason = ReadText("reason", nullable: false);
            var invalidation = ReadText("invalidation", nullable: true);
            var fields = new AiParsedFields(status, market, orderType, side, referencePrice, referenceTime,
                entryPrice, takeProfit, stopLoss, reason, invalidation);

            if (market is not null && !string.Equals(market, request.Market, StringComparison.Ordinal))
                reasons.Add($"Рынок {market} не совпадает с запрошенным {request.Market}.");
            if (orderType is not null && !string.Equals(orderType, request.OrderType, StringComparison.Ordinal))
                reasons.Add($"Тип входа {orderType} не совпадает с запрошенным {request.OrderType}.");
            if (status is not ("SETUP" or "NO_SETUP"))
                reasons.Add("Поле status должно быть SETUP или NO_SETUP.");
            if (referenceTime is not null && !IsUtcTimestamp(referenceTime))
                reasons.Add("reference_time_utc должен быть временем ISO 8601 в UTC либо null.");

            if (status == "SETUP")
            {
                if (side is not ("LONG" or "SHORT"))
                    reasons.Add("Для SETUP поле side должно быть LONG или SHORT.");
                RequirePositive(referencePrice, "reference_price", reasons);
                RequirePositive(takeProfit, "take_profit", reasons);
                RequirePositive(stopLoss, "stop_loss", reasons);
                if (string.IsNullOrWhiteSpace(invalidation))
                    reasons.Add("Для SETUP требуется условие invalidation.");

                if (request.OrderType == "MARKET")
                {
                    if (entryPrice is not null)
                        reasons.Add("Для Market поле entry_price должно быть null; цена фактического исполнения неизвестна.");
                }
                else
                {
                    RequirePositive(entryPrice, "entry_price", reasons);
                }

                var anchor = request.OrderType == "MARKET" ? referencePrice : entryPrice;
                if (anchor is > 0 && takeProfit is > 0 && stopLoss is > 0)
                {
                    var ordered = side switch
                    {
                        "LONG" => stopLoss < anchor && anchor < takeProfit,
                        "SHORT" => takeProfit < anchor && anchor < stopLoss,
                        _ => false
                    };
                    if (!ordered)
                        reasons.Add($"Порядок stop_loss, опорной цены и take_profit неверен для {side ?? "неизвестной стороны"}.");
                }
            }
            else if (status == "NO_SETUP")
            {
                if (side is not null)
                    reasons.Add("Для NO_SETUP поле side должно быть null.");
                if (entryPrice is not null || takeProfit is not null || stopLoss is not null)
                    reasons.Add("Для NO_SETUP entry_price, take_profit и stop_loss должны быть null.");
                if (referencePrice is <= 0)
                    reasons.Add("reference_price для NO_SETUP должен быть положительным числом или null.");
                if (invalidation is not null)
                    reasons.Add("Для NO_SETUP поле invalidation должно быть null.");
            }

            var freshness = referenceTime is null
                ? AiFreshnessState.Unknown
                : AiFreshnessState.TimestampPresentNotAssessed;
            var outcome = reasons.Count != 0
                ? AiTestOutcome.IncompleteOrInvalid
                : status == "SETUP" ? AiTestOutcome.CompleteSetup : AiTestOutcome.ValidNoSetup;
            return (outcome, fields, freshness, reasons);
        }
    }

    private static void RequirePositive(decimal? value, string field, ICollection<string> reasons)
    {
        if (value is null or <= 0)
            reasons.Add($"Поле {field} должно быть положительным числом.");
    }

    private static bool IsUtcTimestamp(string value)
    {
        if (!Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$", RegexOptions.CultureInvariant))
            return false;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)
            && timestamp.Offset == TimeSpan.Zero;
    }
}

internal static class AiTestBatchRunner
{
    internal static async Task<AiTestBatchReport> RunAsync(
        IReadOnlyList<AiAnalysisRequest> requests,
        IAiAnalysisSource source,
        IProgress<AiTestProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
            throw new ArgumentException("A batch must contain at least one request.", nameof(requests));

        var started = DateTimeOffset.UtcNow;
        var results = new List<AiTestResult>(requests.Count);
        var processed = 0;
        foreach (var request in requests)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                var notStarted = new AiTestResult(request, AiTestOutcome.NotStarted, null, null, null, null,
                    AiFreshnessState.Unknown, ["Запрос не запускался после отмены пакета."]);
                results.Add(notStarted);
                progress?.Report(new AiTestProgress(processed, requests.Count, request.Asset, notStarted.Outcome));
                continue;
            }

            var requestStarted = DateTimeOffset.UtcNow;
            AiTestResult result;
            try
            {
                var response = await source.AnalyzeAsync(request, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
                var responseReceived = DateTimeOffset.UtcNow;
                var assessment = AiResponseAssessor.Assess(request, response);
                var trustedFinalAnswer = response.ResponseRequestId == request.Id && response.IsFinalStream
                    ? response.RawText
                    : null;
                result = new AiTestResult(request, assessment.Outcome, requestStarted, responseReceived,
                    trustedFinalAnswer, assessment.Fields, assessment.Freshness, assessment.Reasons);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = new AiTestResult(request, AiTestOutcome.Cancelled, requestStarted, null, null, null,
                    AiFreshnessState.Unknown, ["Запрос отменён до получения завершённого ответа."]);
            }
            catch (TimeoutException)
            {
                result = new AiTestResult(request, AiTestOutcome.Timeout, requestStarted, null, null, null,
                    AiFreshnessState.Unknown, ["Истёк тайм-аут запроса."]);
            }
            catch (Exception exception)
            {
                result = new AiTestResult(request, AiTestOutcome.RequestError, requestStarted, null, null, null,
                    AiFreshnessState.Unknown, [$"Ошибка источника запроса: {exception.GetType().Name}."]);
            }

            results.Add(result);
            processed++;
            progress?.Report(new AiTestProgress(processed, requests.Count, request.Asset, result.Outcome));
        }

        return new AiTestBatchReport(Guid.NewGuid(), started, DateTimeOffset.UtcNow, results);
    }
}

internal sealed class AiTestReportStore
{
    private readonly string _directory;
    private readonly Func<string, string, Task>? _writeOverride;

    internal AiTestReportStore(string directory, Func<string, string, Task>? writeOverride = null)
    {
        _directory = directory;
        _writeOverride = writeOverride;
    }

    internal static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrueNorth TradeFlow", "TestReports");

    internal async Task<string> SaveAsync(AiTestBatchReport report)
    {
        Directory.CreateDirectory(_directory);
        var fileName = $"ai-test-report-{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmssfff'Z'}-{Guid.NewGuid():N}.md";
        var path = Path.Combine(_directory, fileName);
        var content = AiTestReportFormatter.Format(report);
        if (_writeOverride is not null)
            await _writeOverride(path, content).ConfigureAwait(false);
        else
            WriteAtomically(path, content);
        return path;
    }

    private static void WriteAtomically(string path, string content)
    {
        var temporaryPath = path + ".tmp";
        try
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

internal static class AiTestReportFormatter
{
    internal static string Format(AiTestBatchReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Локальный синтетический AI-тест");
        builder.AppendLine();
        builder.AppendLine("Все запросы и ответы в этом отчёте созданы синтетическим источником внутри приложения. На сайт, AI, сеть, аккаунт или торговые функции данные не отправлялись.");
        builder.AppendLine("Оценка подтверждает только полноту формата и данных; это не торговое решение. Свежесть указана отдельно. Время получения ответа не является временем рыночных данных.");
        builder.AppendLine();
        builder.AppendLine($"Пакет: `{report.Id:N}`");
        builder.AppendLine($"Начат UTC: {FormatTime(report.StartedUtc)}  ");
        builder.AppendLine($"Завершён UTC: {FormatTime(report.CompletedUtc)}");
        builder.AppendLine();
        builder.AppendLine($"Всего запросов: **{report.Results.Count}**");
        foreach (var outcome in Enum.GetValues<AiTestOutcome>())
            builder.AppendLine($"- {OutcomeLabel(outcome)}: {report.Count(outcome)}/{report.Results.Count}");

        foreach (var result in report.Results)
        {
            builder.AppendLine();
            builder.AppendLine($"## {EscapeTable(result.Request.Asset)} · {EscapeTable(result.Request.EntryType)}");
            builder.AppendLine();
            builder.AppendLine($"- Рынок: `{EscapeTable(result.Request.Market)}`");
            builder.AppendLine($"- Шаблон: {EscapeTable(result.Request.TemplateVersion)} / {EscapeTable(result.Request.EntryType)} ({EscapeTable(result.Request.OrderType)})");
            builder.AppendLine($"- Время запроса UTC: {FormatOptionalTime(result.RequestStartedUtc)}");
            builder.AppendLine($"- Время получения ответа UTC: {FormatOptionalTime(result.ResponseReceivedUtc)}");
            builder.AppendLine($"- Итог: **{OutcomeLabel(result.Outcome)}**");
            builder.AppendLine($"- Свежесть: {FreshnessLabel(result.Freshness)}");
            builder.AppendLine($"- Причины: {EscapeTable(result.Reasons.Count == 0 ? "Замечаний формата нет." : string.Join("; ", result.Reasons))}");
            builder.AppendLine();
            builder.AppendLine("Поля ответа:");
            builder.AppendLine();
            builder.AppendLine("| Поле | Значение |");
            builder.AppendLine("| --- | --- |");
            foreach (var (name, value) in GetFields(result.Fields))
                builder.AppendLine($"| `{name}` | {EscapeTable(value)} |");
            builder.AppendLine();
            builder.AppendLine("Завершённый сырой ответ:");
            builder.AppendLine();
            builder.AppendLine(FormatRawAnswer(result.RawFinalAnswer));
        }

        return builder.ToString();
    }

    internal static string OutcomeLabel(AiTestOutcome outcome) => outcome switch
    {
        AiTestOutcome.CompleteSetup => "SETUP: полно по формату и данным, не торговое решение",
        AiTestOutcome.ValidNoSetup => "NO_SETUP: валидный ответ без сетапа",
        AiTestOutcome.IncompleteOrInvalid => "Неполный или неверный ответ",
        AiTestOutcome.Inconclusive => "Неубедительный ответ",
        AiTestOutcome.RequestError => "Ошибка запроса",
        AiTestOutcome.Timeout => "Тайм-аут",
        AiTestOutcome.Cancelled => "Отменённый запрос",
        AiTestOutcome.NotStarted => "Не запущен после отмены",
        _ => outcome.ToString()
    };

    private static IEnumerable<(string Name, string Value)> GetFields(AiParsedFields? fields)
    {
        if (fields is null)
        {
            foreach (var name in new[] { "status", "market", "order_type", "side", "reference_price", "reference_time_utc", "entry_price", "take_profit", "stop_loss", "reason", "invalidation" })
                yield return (name, "не извлечено");
            yield break;
        }

        yield return ("status", fields.Status ?? "null");
        yield return ("market", fields.Market ?? "null");
        yield return ("order_type", fields.OrderType ?? "null");
        yield return ("side", fields.Side ?? "null");
        yield return ("reference_price", FormatDecimal(fields.ReferencePrice));
        yield return ("reference_time_utc", fields.ReferenceTimeUtc ?? "null");
        yield return ("entry_price", FormatDecimal(fields.EntryPrice));
        yield return ("take_profit", FormatDecimal(fields.TakeProfit));
        yield return ("stop_loss", FormatDecimal(fields.StopLoss));
        yield return ("reason", fields.Reason ?? "null");
        yield return ("invalidation", fields.Invalidation ?? "null");
    }

    private static string FormatRawAnswer(string? raw)
    {
        if (raw is null)
            return "_(ответ отсутствует)_";

        var safe = EscapeControls(raw);
        var longestFence = 0;
        var currentFence = 0;
        foreach (var character in safe)
        {
            if (character == '`')
                longestFence = Math.Max(longestFence, ++currentFence);
            else
                currentFence = 0;
        }
        var fence = new string('`', Math.Max(3, longestFence + 1));
        return $"{fence}text\n{safe}\n{fence}";
    }

    private static string EscapeTable(string value) => EscapeControls(value)
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("\r\n", "<br>", StringComparison.Ordinal)
        .Replace("\r", "<br>", StringComparison.Ordinal)
        .Replace("\n", "<br>", StringComparison.Ordinal);

    private static string EscapeControls(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\n' or '\r')
                builder.Append(character);
            else if (char.IsControl(character) || character is '\u2028' or '\u2029')
                builder.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
            else
                builder.Append(character);
        }
        return builder.ToString();
    }

    private static string FreshnessLabel(AiFreshnessState freshness) => freshness switch
    {
        AiFreshnessState.Unknown => "неизвестна: timestamp отсутствует или не подтверждён",
        AiFreshnessState.TimestampPresentNotAssessed => "timestamp указан; возраст не оценивался, порог не задан",
        _ => freshness.ToString()
    };

    private static string FormatDecimal(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";
    private static string FormatOptionalTime(DateTimeOffset? value) => value is null ? "нет" : FormatTime(value.Value);
    private static string FormatTime(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
