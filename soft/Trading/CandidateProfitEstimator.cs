using TrueNorth.TradeFlow.Account;
using TrueNorth.TradeFlow.Risk;

namespace TrueNorth.TradeFlow.Trading;

internal sealed record CandidateProfitEstimate(
    string Currency,
    decimal GrossProfit,
    decimal EntryFee,
    decimal ExitFee,
    decimal AdverseFunding,
    decimal NetProfit);

internal static class CandidateProfitEstimator
{
    internal static bool TrySelectWinner(
        IReadOnlyList<CandidateProfitEstimate> estimates,
        out int winnerIndex,
        out bool currenciesDiffer)
    {
        winnerIndex = -1;
        currenciesDiffer = estimates.Select(item => item.Currency).Distinct(StringComparer.Ordinal).Skip(1).Any();
        if (currenciesDiffer)
            return false;

        for (var index = 0; index < estimates.Count; index++)
            if (estimates[index].NetProfit > 0m
                && (winnerIndex < 0 || estimates[index].NetProfit > estimates[winnerIndex].NetProfit))
                winnerIndex = index;
        return winnerIndex >= 0;
    }

    internal static bool TryCalculate(
        EntryEligibilitySetup setup,
        string? currency,
        decimal? entryPrice,
        decimal? quantity,
        decimal? baseQuantity,
        RiskInstrument instrument,
        out CandidateProfitEstimate? estimate,
        out string reason)
    {
        estimate = null;
        reason = "Допуск 007 не вернул полные данные для сравнения.";
        if (entryPrice is not > 0m || quantity is not > 0m
            || baseQuantity is not > 0m || setup.TakeProfit is not > 0m
            || setup.Direction is not { } direction)
            return false;

        var entry = entryPrice.Value;
        var takeProfit = setup.TakeProfit.Value;
        var multiplier = instrument.ContractMultiplier;
        var funding = instrument.Funding;
        if (string.IsNullOrWhiteSpace(currency) || currency != instrument.Identity.QuoteCurrency
            || instrument.Identity.BaseAsset != setup.Asset
            || instrument.PriceTick is not > 0m || takeProfit % instrument.PriceTick.Value != 0m
            || setup.PriceUnit != $"{currency}/{instrument.Identity.BaseAsset}"
            || multiplier is not > 0m
            || instrument.EntryFeeRate is not >= 0m or > 1m
            || instrument.ExitFeeRate is not >= 0m or > 1m
            || funding is null || funding.RatePerInterval is null
            || funding.PositiveRateMeansLongPays is null || funding.IntervalHours is not > 0m
            || funding.Price is not > 0m || funding.RateUnit != "fraction/interval"
            || funding.IntervalUnit != "hours" || funding.PriceUnit != setup.PriceUnit)
            return false;

        if (direction == PositionDirection.Long && takeProfit <= entry
            || direction == PositionDirection.Short && takeProfit >= entry)
        {
            reason = "Допустимая цена take-profit не соответствует направлению сетапа.";
            return false;
        }

        try
        {
            var baseAmount = baseQuantity.Value;
            if (checked(quantity.Value * multiplier.Value) != baseAmount)
            {
                reason = "Округлённое количество не совпадает с базовым объёмом контракта.";
                return false;
            }
            var gross = direction == PositionDirection.Long
                ? baseAmount * (takeProfit - entry)
                : baseAmount * (entry - takeProfit);
            var entryFee = baseAmount * entry * instrument.EntryFeeRate!.Value;
            var exitFee = baseAmount * takeProfit * instrument.ExitFeeRate!.Value;
            var directionSign = direction == PositionDirection.Long ? 1m : -1m;
            var payerSign = funding.PositiveRateMeansLongPays.Value ? 1m : -1m;
            var adverseRate = Math.Max(directionSign * funding.RatePerInterval.Value * payerSign, 0m);
            var adverseFunding = baseAmount * funding.Price.Value * adverseRate
                * (24m / funding.IntervalHours.Value);

            estimate = new(currency, gross, entryFee, exitFee, adverseFunding,
                gross - entryFee - exitFee - adverseFunding);
        }
        catch (OverflowException)
        {
            reason = "Оценка денежных расходов не помещается в допустимый диапазон.";
            return false;
        }
        reason = string.Empty;
        return true;
    }
}
