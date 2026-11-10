using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TrueNorth.TradeFlow.Account;
using TrueNorth.TradeFlow.Risk;

namespace TrueNorth.TradeFlow.Verification;

internal static class EntryEligibilityChecks
{
    private const string VenueA = "Scenario Venue A";
    private const string VenueB = "Scenario Venue B";

    internal static void CheckCore(Action<bool, string> check)
    {
        var input = EntryEligibilityExample.Create();
        var ready = EntryEligibilityEvaluator.Evaluate(input);
        var selected = ready.SelectedPool;
        check(ready.Status == EntryEligibilityStatus.ScenarioReady && selected?.Venue == VenueB,
            "the larger venue with an unreadable stop is skipped for the confirmed lower venue");
        check(ready.Pools.Single(pool => pool.Venue == VenueA).Status == EntryEligibilityStatus.InsufficientData
              && ready.Pools.Single(pool => pool.Venue == VenueA).Reasons.Any(reason => reason.Contains("стоп", StringComparison.OrdinalIgnoreCase)),
            "an unreadable stop blocks only its known venue and keeps a local reason");
        check(selected?.FullBalance == 1000m && selected.SizingBalance == 1000m
              && selected.MarginBalancePercent == 10m && selected.Leverage == 1m
              && selected.Quantity == 2m && selected.BaseQuantity == 2m && selected.Notional == 100m,
            "the fixture keeps the full balance, sizing base, allocated percent and rounded contract size distinct");
        check(selected?.AvailableMargin == 150m && selected.InitialMargin == 100m
              && selected.RequiredMargin == 100.05m,
            "available margin is checked separately from additional initial margin and entry fees");
        check(selected?.StressExposureCount == 2
              && selected.IncrementalCandidateEntryFee == 0.05m
              && selected.TotalStressEntryFees == 0.05m
              && selected.ExitFees == 0.1001m && selected.AdverseFunding == 0.48m
              && selected.StressEquity == 977.3699m && selected.Maintenance == 5.005m
              && selected.StressReserve == 972.3649m && selected.AdditionalHeadroom == 10m
              && selected.ReserveAfterHeadroom == 962.3649m,
            "the §5.4 simultaneous-stop example matches every fee, funding, equity and maintenance amount");
        check(input.Settings.Settings.MarginBalancePercent == 10m && input.Settings.Settings.Leverage == 1m
              && input.Settings.Settings.Assets.SequenceEqual(["SOL"])
              && input.Pools.Single(pool => pool.Account.Venue == VenueB).Account.History.State == AccountReadState.NotRead,
            "explicit fixture settings are read without mutation and unread history is not a required risk input");
        check(selected?.Contract == "SOL-PERP" && selected.QuoteCurrency == "USDC"
              && selected.QuantityUnit == "contract" && selected.BaseQuantityUnit == "SOL"
              && selected.EntryPrice == 50m && selected.Leverage == 1m
              && selected.AccountSource == EntryEligibilityExample.SourceName
              && selected.RulesSource == EntryEligibilityExample.SourceName
              && selected.AccountObservedAt == input.EvaluatedAt.AddSeconds(-20),
            "the result retains exact pool, market, units, source and observed times");
        check(selected?.EvidenceTimes.Any(evidence => evidence.Subject == "mark позиции BTC-PERP"
                      && evidence.Source == EntryEligibilityExample.SourceName
                      && evidence.ObservedAt == input.EvaluatedAt.AddSeconds(-2)) == true
              && selected.EvidenceTimes.Any(evidence => evidence.Subject == "запас maintenance сценария"
                  && evidence.ObservedAt == input.EvaluatedAt),
            "the pool result retains the source and observed time for each risk input, including the explicit fixture buffer");

        var singleVenueInput = input with { Pools = [input.Pools.Single(pool => pool.Account.Venue == VenueB)] };
        var lowerAvailable = UpdatePool(singleVenueInput, VenueB, pool => WithBalances(pool, available: 90m));
        var lowMarginResult = EntryEligibilityEvaluator.Evaluate(lowerAvailable);
        check(lowMarginResult.Status == EntryEligibilityStatus.Waiting && lowMarginResult.SelectedPool is null
              && lowMarginResult.Pools.Single(pool => pool.Venue == VenueB).StressEquity == selected?.StressEquity
              && lowMarginResult.Pools.Single(pool => pool.Venue == VenueB).RequiredMargin == selected?.RequiredMargin,
            "insufficient available margin blocks admission while the same stress equity remains unchanged");
        var zeroAvailable = UpdatePool(singleVenueInput, VenueB, pool => WithBalances(pool, available: 0m));
        var zeroAvailableResult = EntryEligibilityEvaluator.Evaluate(zeroAvailable);
        check(zeroAvailableResult.Status == EntryEligibilityStatus.Waiting
              && zeroAvailableResult.Pools.Single(pool => pool.Venue == VenueB).AvailableMargin == 0m,
            "a confirmed zero available margin remains a known amount and blocks the proposed entry");

        var higherHeadroom = UpdatePool(singleVenueInput, VenueB, pool => pool with
        {
            AdditionalMaintenanceHeadroom = Evidence(1000m, "USDC", input.EvaluatedAt)
        });
        var headroomResult = EntryEligibilityEvaluator.Evaluate(higherHeadroom);
        check(headroomResult.Status == EntryEligibilityStatus.Waiting && headroomResult.SelectedPool is null
              && headroomResult.Pools.Single(pool => pool.Venue == VenueB).StressEquity == selected?.StressEquity
              && headroomResult.Pools.Single(pool => pool.Venue == VenueB).ReserveAfterHeadroom == -27.6351m,
            "a fixture maintenance buffer can independently fail at the reserve boundary without changing E_stress");
        var staleHeadroom = UpdatePool(singleVenueInput, VenueB, pool => pool with
        {
            AdditionalMaintenanceHeadroom = Evidence(10m, "USDC", input.EvaluatedAt.AddMinutes(-6))
        });
        check(EntryEligibilityEvaluator.Evaluate(staleHeadroom).Status == EntryEligibilityStatus.InsufficientData,
            "a missing or stale fixture buffer cannot be passed off as a current maintenance headroom input");

        var negativePnl = WithPosition(input, VenueB, 0, position => position with
        {
            UnrealizedPnl = new MoneyAmount(-30m, "USDC")
        });
        negativePnl = WithBalances(negativePnl, VenueB, equity: 970m);
        var negativePnlResult = EntryEligibilityEvaluator.Evaluate(negativePnl);
        check(negativePnlResult.SelectedPool?.SizingBalance == 970m
              && negativePnlResult.SelectedPool.MarginBalancePercent == 10m
              && negativePnlResult.SelectedPool.Quantity == 1.94m,
            "aggregate negative unrealized PnL lowers the sizing base before applying the saved percentage");

        var positivePnl = WithPosition(input, VenueB, 0, position => position with
        {
            UnrealizedPnl = new MoneyAmount(30m, "USDC")
        });
        positivePnl = WithBalances(positivePnl, VenueB, equity: 1030m);
        var positivePnlResult = EntryEligibilityEvaluator.Evaluate(positivePnl);
        check(positivePnlResult.SelectedPool?.SizingBalance == 1000m
              && positivePnlResult.SelectedPool.Quantity == 2m,
            "aggregate positive unrealized PnL does not enlarge the sizing base");

        var lowerMarketMaximum = WithSettings(input, leverage: 10m);
        lowerMarketMaximum = UpdatePool(lowerMarketMaximum, VenueB, pool => WithInstrument(pool, "SOL-PERP",
            instrument => instrument with { MaximumLeverage = 2m }) with
        {
            CandidateStopCoveredBaseQuantity = Evidence(4m, "SOL", input.EvaluatedAt.AddSeconds(-20))
        });
        var maxLeverageResult = EntryEligibilityEvaluator.Evaluate(lowerMarketMaximum);
        check(maxLeverageResult.SelectedPool?.Leverage == 2m
              && maxLeverageResult.SelectedPool.Quantity == 4m
              && maxLeverageResult.SelectedPool.Notional == 200m,
            "applied leverage uses the lower of the saved leverage and the confirmed market maximum");

        var unavailableLeverage = WithSettings(singleVenueInput, leverage: 1.5m);
        unavailableLeverage = UpdatePool(unavailableLeverage, VenueB, pool => pool with
        {
            CandidateStopCoveredBaseQuantity = Evidence(3m, "SOL", input.EvaluatedAt.AddSeconds(-20))
        });
        var unavailableLeverageResult = EntryEligibilityEvaluator.Evaluate(unavailableLeverage);
        var leveragePool = unavailableLeverageResult.Pools.Single(pool => pool.Venue == VenueB);
        check(unavailableLeverageResult.Status == EntryEligibilityStatus.Waiting
              && unavailableLeverageResult.SelectedPool is null
              && leveragePool.Leverage == 1.5m
              && leveragePool.Reasons.Any(reason => reason.Contains("шаг", StringComparison.OrdinalIgnoreCase)),
            "a requested leverage outside the confirmed one-unit market step stays unchanged and blocks admission");

        var belowMinimum = UpdatePool(singleVenueInput, VenueB, pool => WithInstrument(pool, "SOL-PERP",
            instrument => instrument with { MinimumQuantity = 3m }));
        var minimumResult = EntryEligibilityEvaluator.Evaluate(belowMinimum);
        check(minimumResult.Status == EntryEligibilityStatus.Waiting && minimumResult.SelectedPool is null
              && minimumResult.Pools.Single(pool => pool.Venue == VenueB).Quantity == 2m
              && minimumResult.Pools.Single(pool => pool.Venue == VenueB).Reasons.Any(reason => reason.Contains("ниже минимума", StringComparison.Ordinal)),
            "rounding below the market minimum blocks the unchanged configured allocation");

        var twoVenueReady = MakeVenueAReady(input);
        var venueBindingFallback = UpdatePool(twoVenueReady, VenueB, pool => pool with
        {
            SetupBinding = pool.SetupBinding! with { Contract = "BTC-PERP" }
        });
        check(EntryEligibilityEvaluator.Evaluate(venueBindingFallback).SelectedPool?.Venue == VenueA,
            "the higher-ranked exact venue is selected while a different venue with a mismatched contract is skipped");
        var bothBindingsWrong = UpdatePool(venueBindingFallback, VenueA, pool => pool with
        {
            SetupBinding = pool.SetupBinding! with { Contract = "ETH-PERP" }
        });
        check(EntryEligibilityEvaluator.Evaluate(bothBindingsWrong).SelectedPool is null,
            "venue fallback requires a separately confirmed exact contract mapping on the candidate venue");

        var tierBoundary = UpdatePool(input, VenueB, pool => pool with { Instruments = pool.Instruments.Select(instrument => instrument with
        {
            MaintenanceTiers =
            [
                new RiskMaintenanceTier(89.1m, "USDC", 0.1m, "fraction", 0m, "USDC"),
                new RiskMaintenanceTier(null, "USDC", 0.05m, "fraction", 2m, "USDC")
            ]
        }).ToArray() });
        var tierResult = EntryEligibilityEvaluator.Evaluate(tierBoundary);
        check(tierResult.SelectedPool?.Maintenance == 12.465m,
            "maintenance selects the inclusive first-tier boundary and the second tier deduction for SOL");
        var aboveTierBoundary = UpdatePool(tierBoundary, VenueB, pool => pool with { Instruments = pool.Instruments.Select(instrument => instrument with
        {
            MaintenanceTiers =
            [
                new RiskMaintenanceTier(89.09m, "USDC", 0.1m, "fraction", 0m, "USDC"),
                new RiskMaintenanceTier(null, "USDC", 0.05m, "fraction", 2m, "USDC")
            ]
        }).ToArray() });
        check(EntryEligibilityEvaluator.Evaluate(aboveTierBoundary).SelectedPool?.Maintenance == 6.01m,
            "moving the maintenance boundary just below stressed BTC notional selects the deducted upper tier");

        var favorableFunding = UpdatePool(input, VenueB, pool => pool with
        {
            Instruments = pool.Instruments.Select(instrument => instrument with
            {
                Funding = instrument.Funding! with
                {
                    RatePerInterval = instrument.Identity.Contract == "BTC-PERP" ? -0.0001m : 0.0001m
                }
            }).ToArray()
        });
        var favorableFundingResult = EntryEligibilityEvaluator.Evaluate(favorableFunding);
        check(favorableFundingResult.SelectedPool?.AdverseFunding == 0m
              && favorableFundingResult.SelectedPool.StressEquity == 977.8499m,
            "favorable funding is not credited as income to the stress result");

        var twoHourFunding = UpdatePool(input, VenueB, pool => pool with
        {
            Instruments = pool.Instruments.Select(instrument => instrument with
            {
                Funding = instrument.Funding! with
                {
                    RatePerInterval = instrument.Identity.Contract == "BTC-PERP" ? 0.0002m : -0.0002m,
                    IntervalHours = 2m
                }
            }).ToArray()
        });
        check(EntryEligibilityEvaluator.Evaluate(twoHourFunding).SelectedPool?.AdverseFunding == 0.48m,
            "adverse funding scales by the explicit interval over the 24-hour horizon");

        var partialFill = WithPartialOpeningAndClose(input);
        var partialResult = EntryEligibilityEvaluator.Evaluate(partialFill);
        var partialSelected = partialResult.SelectedPool;
        var incompletePartialStress = UpdatePool(partialFill, VenueB, pool => pool with
        {
            CandidateStopCoveredBaseQuantity = Evidence(1m, "SOL", input.EvaluatedAt.AddSeconds(-20))
        });
        var incompletePartialResult = EntryEligibilityEvaluator.Evaluate(incompletePartialStress);
        var incompletePartialPool = incompletePartialResult.Pools.Single(pool => pool.Venue == VenueB);
        check(partialResult.Status == EntryEligibilityStatus.ScenarioReady
              && partialSelected?.StressExposureCount == 3
              && partialSelected.IncrementalCandidateEntryFee == 0.05m
              && partialSelected.TotalStressEntryFees == 0.075m
              && partialSelected.RequiredMargin == 120.05m
              && partialSelected.StressEquity == 977.3449m
              && partialSelected.ExitFees == 0.1001m
              && partialSelected.AdverseFunding == 0.48m
              && incompletePartialResult.SelectedPool is null
              && incompletePartialPool.Status == EntryEligibilityStatus.InsufficientData
              && incompletePartialPool.IncrementalCandidateEntryFee == 0.05m
              && incompletePartialPool.TotalStressEntryFees is null
              && incompletePartialPool.Reasons.Any(reason => reason.Contains("стоп нового входа", StringComparison.OrdinalIgnoreCase)),
            "partial fill keeps the candidate fee separate from the complete stress total, while missing candidate stop coverage leaves that total unknown");

        var unknownOpeningRole = UpdatePool(partialFill, VenueB, pool => pool with
        {
            Orders = pool.Orders.Select(order => order with
            {
                Role = order.Role! with { Value = RiskOrderRole.Unknown }
            }).ToArray()
        });
        var unknownRoleResult = EntryEligibilityEvaluator.Evaluate(unknownOpeningRole);
        check(unknownRoleResult.Status == EntryEligibilityStatus.InsufficientData
              && unknownRoleResult.SelectedPool is null
              && unknownRoleResult.Reasons.Any(reason => reason.Contains("назначение заявки", StringComparison.OrdinalIgnoreCase)),
            "unknown opening-versus-protection classification blocks global exposure uniqueness");

        var undefinedOpeningRole = UpdatePool(partialFill, VenueB, pool => pool with
        {
            Orders = pool.Orders.Select(order => order with
            {
                Role = order.Role! with { Value = (RiskOrderRole)99 }
            }).ToArray()
        });
        var undefinedRoleResult = EntryEligibilityEvaluator.Evaluate(undefinedOpeningRole);
        check(undefinedRoleResult.Status == EntryEligibilityStatus.InsufficientData
              && undefinedRoleResult.SelectedPool is null
              && undefinedRoleResult.Reasons.Any(reason => reason.Contains("назначение заявки", StringComparison.OrdinalIgnoreCase)),
            "an order role outside the defined enum blocks global exposure uniqueness");

        var duplicateAsset = WithAddedPosition(input, VenueA, new OpenPosition(
            "SOL", "SOL-PERP", PositionDirection.Long, 1m, "contract", new MoneyAmount(0m, "USDC"),
            PositionOwnership.Manual, "Синтетическая ручная позиция."), new RiskPositionInput(
                new OpenPosition("SOL", "SOL-PERP", PositionDirection.Long, 1m, "contract", new MoneyAmount(0m, "USDC"),
                    PositionOwnership.Manual, "Синтетическая ручная позиция."),
                "sol-open", EntryEligibilityExample.Evidence(50m, "USDC/SOL", input.EvaluatedAt.AddSeconds(-2)),
                EntryEligibilityExample.Evidence(45m, "USDC/SOL", input.EvaluatedAt.AddSeconds(-20)),
                EntryEligibilityExample.Evidence(1m, "SOL", input.EvaluatedAt.AddSeconds(-20))));
        var duplicateResult = EntryEligibilityEvaluator.Evaluate(duplicateAsset);
        check(duplicateResult.SelectedPool is null
              && duplicateResult.Status == EntryEligibilityStatus.Waiting
              && duplicateResult.Reasons.Any(reason => reason.Contains("уже открыт", StringComparison.Ordinal)),
            "a manual position on any venue blocks a duplicate base asset regardless of ownership");

        var openSolOrder = new OpenOrder("SOL", "SOL-PERP", PositionDirection.Short,
            1m, 0m, "contract", "Open", PositionOwnership.Unknown, "открывающий остаток");
        var openSolRiskOrder = new RiskOrderInput(openSolOrder, "sol-opening", Evidence(RiskOrderRole.Opening,
            "role", input.EvaluatedAt.AddSeconds(-20)), Evidence(50m, "USDC/SOL", input.EvaluatedAt.AddSeconds(-2)),
            Evidence(55m, "USDC/SOL", input.EvaluatedAt.AddSeconds(-20)), Evidence(1m, "SOL", input.EvaluatedAt.AddSeconds(-20)),
            Evidence(20m, "USDC", input.EvaluatedAt.AddSeconds(-20)), []);
        var duplicateOrder = UpdatePool(input, VenueA, pool => pool with
        {
            Account = pool.Account with
            {
                Orders = AccountSection<IReadOnlyList<OpenOrder>>.Available(
                    [openSolOrder], pool.Account.ObservedAt, pool.Account.Source)
            },
            Orders = [openSolRiskOrder]
        });
        var duplicateOrderResult = EntryEligibilityEvaluator.Evaluate(duplicateOrder);
        check(duplicateOrderResult.SelectedPool is null
              && duplicateOrderResult.Status == EntryEligibilityStatus.Waiting
              && duplicateOrderResult.Reasons.Any(reason => reason.Contains("Открывающий остаток по активу SOL", StringComparison.Ordinal)),
            "an opening-order remainder for the candidate base asset blocks entry across venues even with unknown ownership");

        var unknownCoverage = input with { AllConnectedExposureCovered = null };
        var unknownCoverageResult = EntryEligibilityEvaluator.Evaluate(unknownCoverage);
        check(unknownCoverageResult.Status == EntryEligibilityStatus.InsufficientData
              && unknownCoverageResult.SelectedPool is null
              && unknownCoverageResult.Reasons.Any(reason => reason.Contains("полнота покрытия", StringComparison.OrdinalIgnoreCase)),
            "unknown global position or order coverage prevents selecting an otherwise passing venue");

        var incompletePositions = UpdatePool(input, VenueB, pool => pool with
        {
            Account = pool.Account with
            {
                Positions = AccountSection<IReadOnlyList<OpenPosition>>.Incomplete(
                    pool.Account.Positions.Value!, pool.Account.ObservedAt, pool.Account.Source,
                    "синтетически неполное чтение", pool.Account.Positions.LastSuccessfulAt)
            }
        });
        var incompleteResult = EntryEligibilityEvaluator.Evaluate(incompletePositions);
        check(incompleteResult.SelectedPool is null && incompleteResult.Status == EntryEligibilityStatus.InsufficientData
              && incompleteResult.Reasons.Any(reason => reason.Contains("не прочитан полностью", StringComparison.OrdinalIgnoreCase)),
            "a partially read mandatory position section remains distinct from a confirmed-empty section");

        var previousBalance = UpdatePool(input, VenueB, pool =>
        {
            var section = pool.Account.Balances;
            return pool with
            {
                Account = pool.Account with
                {
                    Balances = AccountSection<AccountBalance>.PreviousSnapshot(section.Value!, section.ObservedAt!.Value,
                        section.Source, section.LastSuccessfulAt!.Value, "предыдущий снимок")
                }
            };
        });
        check(EntryEligibilityEvaluator.Evaluate(previousBalance).SelectedPool is null,
            "a previous balance section cannot supply a current sizing value");

        var notReadOrders = UpdatePool(input, VenueB, pool => pool with
        {
            Account = pool.Account with { Orders = AccountSection<IReadOnlyList<OpenOrder>>.NotRead(EntryEligibilityExample.SourceName) }
        });
        check(EntryEligibilityEvaluator.Evaluate(notReadOrders).Status == EntryEligibilityStatus.InsufficientData,
            "an unknown order list is not treated as a confirmed empty list");

        var staleAccount = UpdatePool(input, VenueB, pool => pool with
        {
            Account = pool.Account with
            {
                ObservedAt = input.EvaluatedAt.AddMinutes(-6),
                LastSuccessfulReadAt = input.EvaluatedAt.AddMinutes(-6)
            }
        });
        check(EntryEligibilityEvaluator.Evaluate(staleAccount).Status == EntryEligibilityStatus.InsufficientData,
            "a prior account envelope blocks admission under the explicit five-minute fixture policy");

        var staleTargetPosition = new OpenPosition("SOL", "SOL-PERP", PositionDirection.Long,
            1m, "contract", new MoneyAmount(0m, "USDC"), PositionOwnership.Manual, "прошлая тестовая строка");
        var staleTarget = UpdatePool(singleVenueInput, VenueB, pool =>
        {
            var priorAt = input.EvaluatedAt.AddMinutes(-6);
            return pool with
            {
                Account = pool.Account with
                {
                    Positions = AccountSection<IReadOnlyList<OpenPosition>>.PreviousSnapshot(
                        [staleTargetPosition], priorAt, EntryEligibilityExample.SourceName, priorAt, "предыдущий снимок")
                },
                Positions = []
            };
        });
        var staleTargetResult = EntryEligibilityEvaluator.Evaluate(staleTarget);
        check(staleTargetResult.Status == EntryEligibilityStatus.InsufficientData
              && staleTargetResult.SelectedPool is null
              && staleTargetResult.Reasons.All(reason => !reason.Contains("уже открыт", StringComparison.Ordinal)),
            "an unverified prior SOL row reports incomplete coverage without claiming a confirmed duplicate");

        var overflowInput = WithPosition(singleVenueInput, VenueB, 0, position => position with
        {
            UnrealizedPnl = new MoneyAmount(decimal.MaxValue, "USDC")
        });
        var overflowResult = EntryEligibilityEvaluator.Evaluate(overflowInput);
        check(overflowResult.Status == EntryEligibilityStatus.InsufficientData
              && overflowResult.SelectedPool is null
              && overflowResult.Pools.Single(pool => pool.Venue == VenueB).FullBalance == 1000m
              && overflowResult.Pools.Single(pool => pool.Venue == VenueB).Reasons.Any(reason => reason.Contains("decimal", StringComparison.OrdinalIgnoreCase)),
            "decimal overflow returns a local insufficient-data result with known balance intact");

        var stalePrice = UpdatePool(input, VenueB, pool => pool with
        {
            VerifiedLimitPrice = pool.VerifiedLimitPrice! with { ObservedAt = input.EvaluatedAt.AddMinutes(-2) }
        });
        check(EntryEligibilityEvaluator.Evaluate(stalePrice).SelectedPool is null,
            "an expired Limit quote blocks the candidate even when the setup timestamp remains fresh");

        var unknownPriceUnit = UpdatePool(input, VenueB, pool => pool with
        {
            VerifiedLimitPrice = pool.VerifiedLimitPrice! with { Unit = "USD/SOL" }
        });
        check(EntryEligibilityEvaluator.Evaluate(unknownPriceUnit).SelectedPool is null,
            "a price in an unverified quote currency is rejected");

        var wrongPriceMarket = UpdatePool(singleVenueInput, VenueB, pool => pool with
        {
            VerifiedLimitPrice = pool.VerifiedLimitPrice! with { Market = "BTC-PERP" }
        });
        check(EntryEligibilityEvaluator.Evaluate(wrongPriceMarket).SelectedPool is null,
            "a valid-unit Limit quote from another contract cannot satisfy the selected market");

        var unknownFundingUnit = UpdatePool(input, VenueB, pool => WithInstrument(pool, "SOL-PERP", instrument => instrument with
        {
            Funding = instrument.Funding! with { RateUnit = "unknown" }
        }));
        check(EntryEligibilityEvaluator.Evaluate(unknownFundingUnit).SelectedPool is null,
            "funding with an unknown rate unit cannot produce a positive stress result");

        var wrongMarket = UpdatePool(input, VenueB, pool => pool with
        {
            SetupBinding = pool.SetupBinding! with { SetupMarket = "SOL-USD" }
        });
        check(EntryEligibilityEvaluator.Evaluate(wrongMarket).SelectedPool is null,
            "a venue mapping to a different market identity is not inferred from the same base asset");

        var incompleteSetup = input with { Setup = input.Setup with { State = EntrySetupState.Incomplete } };
        check(EntryEligibilityEvaluator.Evaluate(incompleteSetup).Status == EntryEligibilityStatus.InsufficientData,
            "an incomplete mandatory setup blocks sizing and stress calculation");

        var noSetup = input with { Setup = input.Setup with { State = EntrySetupState.NoSetup } };
        var noSetupResult = EntryEligibilityEvaluator.Evaluate(noSetup);
        check(noSetupResult.SelectedPool is null && noSetupResult.Status == EntryEligibilityStatus.Waiting,
            "a known absent setup is presented as a wait state rather than a positive eligibility result");

        var wrongLevelOrder = input with { Setup = input.Setup with { StopLoss = 45m } };
        check(EntryEligibilityEvaluator.Evaluate(wrongLevelOrder).SelectedPool is null,
            "setup stop and target are validated against side and entry after exact market selection");

        var mismatchedLimit = singleVenueInput with
        {
            Setup = singleVenueInput.Setup with { LimitPrice = 50.01m }
        };
        var mismatchedLimitResult = EntryEligibilityEvaluator.Evaluate(mismatchedLimit);
        check(mismatchedLimitResult.Status == EntryEligibilityStatus.Waiting
              && mismatchedLimitResult.SelectedPool is null
              && mismatchedLimitResult.Pools.Single(pool => pool.Venue == VenueB).EntryPrice is null
              && mismatchedLimitResult.Pools.Single(pool => pool.Venue == VenueB).Reasons.Any(reason => reason.Contains("не совпадает", StringComparison.Ordinal)),
            "a verified Limit price cannot replace a different price in the exact same-market setup");

        var marketInput = input with
        {
            Setup = input.Setup with { EntryType = "Market", LimitPrice = 999m },
            Settings = WithSettings(input, entryType: "Market").Settings
        };
        marketInput = UpdatePool(marketInput, VenueB, pool => pool with
        {
            CurrentMarketPrice = EntryEligibilityExample.Evidence(50m, "USDC/SOL", input.EvaluatedAt.AddSeconds(-1), "SOL-PERP"),
            VerifiedLimitPrice = null
        });
        var marketResult = EntryEligibilityEvaluator.Evaluate(marketInput);
        check(marketResult.SelectedPool?.EntryPriceSource == EntryEligibilityExample.SourceName
              && marketResult.SelectedPool.EntryPrice == 50m
              && marketResult.SelectedPool.EntryPriceObservedAt == input.EvaluatedAt.AddSeconds(-1),
            "Market sizing uses the fresh observation price and ignores an AI-provided Limit price");

        var tie = UpdatePool(input, VenueA, pool => WithBalances(pool, balance: 1000m));
        var tieResult = EntryEligibilityEvaluator.Evaluate(tie);
        check(tieResult.SelectedPool?.Venue == VenueB && tieResult.Status == EntryEligibilityStatus.ScenarioReady,
            "a tied balance does not block the sole venue that passed its local checks");

        var bothReadyTie = MakeVenueAReady(input);
        bothReadyTie = UpdatePool(bothReadyTie, VenueB, pool => WithBalances(pool with
        {
            CandidateStopCoveredBaseQuantity = Evidence(3m, "SOL", input.EvaluatedAt.AddSeconds(-20))
        }, balance: 1500m, equity: 1500m, available: 300m));
        var bothReadyTieResult = EntryEligibilityEvaluator.Evaluate(bothReadyTie);
        check(bothReadyTieResult.SelectedPool is null
              && bothReadyTieResult.Pools.All(pool => pool.Status == EntryEligibilityStatus.ScenarioReady)
              && bothReadyTieResult.Reasons.Any(reason => reason.Contains("равный полный баланс", StringComparison.Ordinal)),
            "equal balances remain ambiguous when two venues both pass the applicable entry checks");

        var blockedAtTie = WithBalances(input.Pools.Single(pool => pool.Account.Venue == VenueB),
            balance: 1500m, equity: 1500m, available: 0m) with
        {
            CandidateStopCoveredBaseQuantity = Evidence(3m, "SOL", input.EvaluatedAt.AddSeconds(-20))
        };
        var lowerVenue = CloneVenue(input.Pools.Single(pool => pool.Account.Venue == VenueB),
            "Scenario Venue C", "Scenario Account C", "USDC cross C", balance: 1000m, available: 150m);
        var skippedFailedTie = input with { Pools = [input.Pools[0], blockedAtTie, lowerVenue] };
        var skippedFailedTieResult = EntryEligibilityEvaluator.Evaluate(skippedFailedTie);
        check(skippedFailedTieResult.SelectedPool?.Venue == "Scenario Venue C",
            "a tied top balance group with only local failures is skipped for the next confirmed venue");

        var multiplePools = input with { Pools = [input.Pools[0], input.Pools[1], input.Pools[1] with
        {
            Account = input.Pools[1].Account with { CollateralPool = "USDC cross B2" }
        }] };
        var multiplePoolsResult = EntryEligibilityEvaluator.Evaluate(multiplePools);
        check(multiplePoolsResult.SelectedPool is null
              && multiplePoolsResult.Reasons.Any(reason => reason.Contains("несколько счетов или пулов", StringComparison.Ordinal)),
            "multiple accounts or collateral pools on one venue do not get an invented tie-break");

        var differentCurrency = UpdatePool(input, VenueA, pool => WithQuoteCurrency(pool, "USD"));
        check(EntryEligibilityEvaluator.Evaluate(differentCurrency).SelectedPool is null,
            "different quote currencies are not combined or converted for venue ranking");

        var invalidSavedSettings = input with { Settings = new SettingsProfile { SelectedMode = "unknown" } };
        var invalidSettingsResult = EntryEligibilityEvaluator.Evaluate(invalidSavedSettings);
        check(invalidSettingsResult.SelectedPool is null
              && invalidSettingsResult.Status == EntryEligibilityStatus.InsufficientData,
            "saved settings pass through the existing validator and invalid or incomplete parameters block evaluation");

        var failedCross = UpdatePool(input, VenueB, pool => WithInstrument(pool, "SOL-PERP", instrument => instrument with
        {
            SupportsCross = false
        }));
        var crossResult = EntryEligibilityEvaluator.Evaluate(failedCross);
        check(crossResult.SelectedPool is null
              && crossResult.Pools.Single(pool => pool.Venue == VenueB).Reasons.Any(reason => reason.Contains("cross-маржа недоступна", StringComparison.Ordinal)),
            "a market that lacks cross support is blocked without an isolated-margin fallback");
    }

    internal static void CheckNativeWpf(Action<bool, string> check, string fixtureRoot)
    {
        if (Application.Current is null)
        {
            var app = new App();
            app.InitializeComponent();
        }

        var settingsPath = Path.Combine(fixtureRoot, "eligibility-wpf", "settings.json");
        var store = new SettingsStore(settingsPath);
        var profile = new SettingsProfile { SelectedMode = "TrueNorth AI setups" };
        check(store.Save(profile).Succeeded, "the eligibility WPF harness saves an isolated settings fixture");
        var profileBytes = File.ReadAllBytes(settingsPath);
        var controller = new WakeProtectionController(false, _ => true);
        var window = new MainWindow(store, store.Load(), controller,
            new TrueNorthSessionChecks.RecordingSessionService());

        try
        {
            window.Width = 760;
            window.Height = 560;
            window.Show();
            window.Activate();
            window.UpdateLayout();
            PumpDispatcher();

            var dashboard = Get<AccountDashboardView>(window, "AccountDashboardPage");
            var badge = Get<Border>(window, "AccountTestDataBadge");
            var scroll = Get<ScrollViewer>(window, "MainScrollViewer");
            check(dashboard.EligibilityPanelElement.Visibility == Visibility.Collapsed
                  && dashboard.EligibilityStatusElement.Text.Length == 0,
                "the risk fixture remains hidden while real account data is unavailable");

            Focus(dashboard.ExampleButton);
            InvokeButton(dashboard.ExampleButton);
            PumpDispatcher();
            window.UpdateLayout();
            dashboard.EligibilityPanelElement.BringIntoView();
            PumpDispatcher();
            check(dashboard.IsShowingExample && dashboard.EligibilityPanelElement.IsVisible
                  && badge.Visibility == Visibility.Visible
                  && dashboard.OpenHistoryButton.IsKeyboardFocused,
                "Button InvokePattern opens the separate risk fixture inside the local example while preserving focus and the test badge");
            check(dashboard.EligibilityStatusElement.Text.Contains("условия сценария выполнены", StringComparison.Ordinal)
                  && dashboard.EligibilitySummaryElement.Text.Contains(VenueB, StringComparison.Ordinal)
                  && dashboard.EligibilityDetails.Contains(972.3649m.ToString("0.################", CultureInfo.CurrentCulture), StringComparison.Ordinal)
                  && dashboard.EligibilityVenueReasonsElement.Text.Contains(VenueA, StringComparison.Ordinal)
                  && dashboard.EligibilityTimesElement.Text.Contains("2026-11-10 09:00:00", StringComparison.Ordinal),
                "the visible panel prioritizes outcome and reason and shows separate sizing, stress, venue and timestamp details");
            check(dashboard.EligibilityPanelElement.IsVisible && dashboard.EligibilityPanelElement.ActualWidth > 0
                  && dashboard.EligibilityPanelElement.ActualHeight > 0
                  && AutomationProperties.GetName(dashboard.EligibilityPanelElement).Contains("отдельный синтетический сценарий", StringComparison.OrdinalIgnoreCase),
                "the risk result has laid out visual bounds and a distinct accessible name");
            var panelBounds = dashboard.EligibilityPanelElement.TransformToAncestor(scroll)
                .TransformBounds(new Rect(0, 0, dashboard.EligibilityPanelElement.ActualWidth,
                    dashboard.EligibilityPanelElement.ActualHeight));
            check(panelBounds.Top >= 0 && panelBounds.Bottom <= scroll.ViewportHeight,
                "the full compact calculation is inside the 760 by 560 scroll viewport after it is brought into view "
                + $"(panel={panelBounds.Top:0}-{panelBounds.Bottom:0}; viewport={scroll.ViewportHeight:0})");
            check(dashboard.EligibilityPanelElement.Focusable == false
                  && dashboard.OpenHistoryButton.Focusable && dashboard.OpenHistoryButton.IsTabStop
                  && dashboard.ExitButton.Focusable && dashboard.ExitButton.IsTabStop,
                "the read-only calculation adds no tab stop and existing example actions remain keyboard reachable");

            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "eligibility-overview-760x560.png"));
            Focus(dashboard.OpenHistoryButton);
            var request = new TraversalRequest(FocusNavigationDirection.Next);
            var reachedExit = false;
            for (var attempt = 0; attempt < 24 && !reachedExit; attempt++)
            {
                if (Keyboard.FocusedElement is not UIElement focused || !focused.MoveFocus(request))
                    break;
                PumpDispatcher();
                reachedExit = dashboard.ExitButton.IsKeyboardFocused;
            }
            check(reachedExit && dashboard.ExitButton.IsVisible && badge.Visibility == Visibility.Visible,
                "WPF MoveFocus traversal from History reaches the visible example exit at 760 by 560");
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "eligibility-focus-exit-760x560.png"));

            var navigation = Get<ListBox>(window, "NavigationList");
            Focus(dashboard.OpenHistoryButton);
            InvokeButton(dashboard.OpenHistoryButton);
            PumpDispatcher();
            check(navigation.SelectedIndex == 4 && dashboard.EligibilityPanelElement.Visibility == Visibility.Visible
                  && badge.Visibility == Visibility.Visible,
                "opening history retains the example calculation state without replacing its overview fixture");
            var history = Get<AccountHistoryView>(window, "AccountHistoryPage");
            Focus(history.BackButton);
            InvokeButton(history.BackButton);
            PumpDispatcher();
            check(navigation.SelectedIndex == 0 && dashboard.EligibilityPanelElement.Visibility == Visibility.Visible
                  && dashboard.OpenHistoryButton.IsKeyboardFocused,
                "returning from history restores the overview focus with the risk result still attached");

            var northCard = dashboard.DisplayedSnapshotCards!.Single(card => card.Venue == "North Perp");
            check(dashboard.EligibilityResult?.Pools.Single(pool => pool.Venue == VenueB).StressExposureCount == 2
                  && northCard.Positions.Count == 3
                  && northCard.PositionStatus.Contains("неполные", StringComparison.OrdinalIgnoreCase),
                "the calculation uses its separate synthetic fixture while the incomplete 006 snapshot remains visible below");

            Focus(dashboard.ExitButton);
            InvokeButton(dashboard.ExitButton);
            PumpDispatcher();
            check(!dashboard.IsShowingExample && dashboard.EligibilityPanelElement.Visibility == Visibility.Collapsed
                  && dashboard.EligibilityStatusElement.Text.Length == 0
                  && dashboard.ExampleButton.IsKeyboardFocused && badge.Visibility == Visibility.Collapsed,
                "exiting the example hides and clears the risk calculation and restores visible-action focus");
            check(File.ReadAllBytes(settingsPath).SequenceEqual(profileBytes)
                  && store.Load().Profile.SelectedMode == "TrueNorth AI setups",
                "opening and closing the local risk example preserves settings bytes exactly");
        }
        finally
        {
            window.AllowApplicationClose();
            window.Close();
            controller.Dispose();
        }
    }

    private static EntryEligibilityInput WithPartialOpeningAndClose(EntryEligibilityInput input)
    {
        var pool = input.Pools.Single(candidate => candidate.Account.Venue == VenueB);
        var original = pool.Positions[0];
        var partialPosition = original.Position with { Quantity = 0.5m, Ownership = PositionOwnership.Unknown };
        var riskPosition = original with
        {
            Position = partialPosition,
            StopCoveredBaseQuantity = original.StopCoveredBaseQuantity! with { Value = 0.5m }
        };
        var partialOrder = new OpenOrder("BTC", "BTC-PERP", PositionDirection.Long,
            1m, 0.5m, "contract", "Partially filled", PositionOwnership.Unknown, "синтетический остаток");
        var closingOrder = new OpenOrder("BTC", "BTC-PERP", PositionDirection.Short,
            0.5m, 0m, "contract", "Open", PositionOwnership.Unknown, "синтетическая защита");
        var orderSection = AccountSection<IReadOnlyList<OpenOrder>>.Available(
            [partialOrder, closingOrder], pool.Account.ObservedAt, EntryEligibilityExample.SourceName);
        var updatedAccount = pool.Account with
        {
            Positions = AccountSection<IReadOnlyList<OpenPosition>>.Available(
                [partialPosition], pool.Account.ObservedAt, EntryEligibilityExample.SourceName),
            Orders = orderSection
        };
        var orderInputs = new RiskOrderInput[]
        {
            new(partialOrder, "btc-order", Evidence(RiskOrderRole.Opening, "role", input.EvaluatedAt.AddSeconds(-20)),
                Evidence(100m, "USDC/BTC", input.EvaluatedAt.AddSeconds(-2)),
                Evidence(90m, "USDC/BTC", input.EvaluatedAt.AddSeconds(-20)),
                Evidence(0.5m, "BTC", input.EvaluatedAt.AddSeconds(-20)),
                Evidence(20m, "USDC", input.EvaluatedAt.AddSeconds(-20)),
                [new RiskOrderFillLink("btc-open", 0.5m)]),
            new(closingOrder, "btc-protection", Evidence(RiskOrderRole.ProtectiveOrClosing, "role", input.EvaluatedAt.AddSeconds(-20)),
                null, null, null, null, [])
        };
        var updatedPool = pool with
        {
            Account = updatedAccount,
            Positions = [riskPosition],
            Orders = orderInputs,
            AvailableMarginIncludesOrderReserves = Evidence(false, "bool", input.EvaluatedAt.AddSeconds(-20))
        };
        return ReplacePool(input, updatedPool);
    }

    private static EntryEligibilityInput MakeVenueAReady(EntryEligibilityInput input) =>
        UpdatePool(input, VenueA, pool =>
        {
            var original = pool.Positions.Single();
            var accountAt = pool.Account.ObservedAt;
            var position = original.Position;
            var updatedRiskPosition = original with
            {
                StopLoss = Evidence(1800m, "USDC/ETH", accountAt),
                StopCoveredBaseQuantity = Evidence(0.1m, "ETH", accountAt)
            };
            return pool with
            {
                Positions = [updatedRiskPosition],
                Account = pool.Account with
                {
                    Protections = AccountSection<IReadOnlyList<PositionProtection>>.Available(
                        [new PositionProtection("ETH-PERP", new MoneyAmount(2200m, "USDC"), new MoneyAmount(1800m, "USDC"))],
                        accountAt, EntryEligibilityExample.SourceName)
                },
                CandidateStopCoveredBaseQuantity = Evidence(3m, "SOL", accountAt)
            };
        });

    private static EntryEligibilityInput WithAddedPosition(
        EntryEligibilityInput input,
        string venue,
        OpenPosition position,
        RiskPositionInput riskPosition) =>
        UpdatePool(input, venue, pool => pool with
        {
            Account = pool.Account with
            {
                Positions = AccountSection<IReadOnlyList<OpenPosition>>.Available(
                    [.. pool.Account.Positions.Value!, position], pool.Account.ObservedAt, pool.Account.Source)
            },
            Positions = [.. pool.Positions, riskPosition]
        });

    private static EntryEligibilityInput WithPosition(
        EntryEligibilityInput input,
        string venue,
        int index,
        Func<OpenPosition, OpenPosition> update) =>
        UpdatePool(input, venue, pool =>
        {
            var positions = pool.Account.Positions.Value!.ToArray();
            positions[index] = update(positions[index]);
            var riskPositions = pool.Positions.ToArray();
            riskPositions[index] = riskPositions[index] with { Position = positions[index] };
            return pool with
            {
                Account = pool.Account with
                {
                    Positions = AccountSection<IReadOnlyList<OpenPosition>>.Available(
                        positions, pool.Account.Positions.ObservedAt!.Value, pool.Account.Positions.Source)
                },
                Positions = riskPositions
            };
        });

    private static EntryEligibilityInput WithBalances(
        EntryEligibilityInput input,
        string venue,
        decimal? balance = null,
        decimal? equity = null,
        decimal? available = null) =>
        UpdatePool(input, venue, pool => WithBalances(pool, balance, equity, available));

    private static RiskPoolInput WithBalances(
        RiskPoolInput pool,
        decimal? balance = null,
        decimal? equity = null,
        decimal? available = null)
    {
        var section = pool.Account.Balances;
        var prior = section.Value!;
        var updatedBalance = balance ?? prior.Balance!.Value;
        var updatedEquity = equity ?? prior.Equity!.Value;
        var updatedAvailable = available ?? prior.AvailableMargin!.Value;
        return pool with
        {
            Account = pool.Account with
            {
                Balances = AccountSection<AccountBalance>.Available(
                    new AccountBalance(new MoneyAmount(updatedBalance, "USDC"),
                        new MoneyAmount(updatedEquity, "USDC"), new MoneyAmount(updatedAvailable, "USDC")),
                    section.ObservedAt!.Value, section.Source)
            }
        };
    }

    private static RiskPoolInput CloneVenue(
        RiskPoolInput pool,
        string venue,
        string accountName,
        string collateralPool,
        decimal balance,
        decimal available)
    {
        var balanced = WithBalances(pool, balance, balance, available);
        return balanced with
        {
            Account = balanced.Account with
            {
                Venue = venue,
                Account = accountName,
                CollateralPool = collateralPool
            },
            Instruments = balanced.Instruments.Select(instrument => instrument with
            {
                Identity = instrument.Identity with { Venue = venue }
            }).ToArray(),
            SetupBinding = balanced.SetupBinding! with { Venue = venue }
        };
    }

    private static RiskPoolInput WithQuoteCurrency(RiskPoolInput pool, string currency)
    {
        var section = pool.Account.Balances;
        var prior = section.Value!;
        return pool with
        {
            Account = pool.Account with
            {
                QuoteCurrency = currency,
                Balances = AccountSection<AccountBalance>.Available(
                    new AccountBalance(new MoneyAmount(prior.Balance!.Value, currency),
                        new MoneyAmount(prior.Equity!.Value, currency), new MoneyAmount(prior.AvailableMargin!.Value, currency)),
                    section.ObservedAt!.Value, section.Source)
            }
        };
    }

    private static RiskPoolInput WithInstrument(
        RiskPoolInput pool,
        string contract,
        Func<RiskInstrument, RiskInstrument> update)
    {
        var instruments = pool.Instruments.Select(instrument =>
            instrument.Identity.Contract == contract ? update(instrument) : instrument).ToArray();
        return pool with { Instruments = instruments };
    }

    private static EntryEligibilityInput WithSettings(
        EntryEligibilityInput input,
        decimal? leverage = null,
        string? entryType = null)
    {
        var settings = input.Settings.Settings.Copy();
        if (leverage is { } leverageValue)
            settings.Leverage = leverageValue;
        if (entryType is not null)
            settings.EntryType = entryType;
        return input with
        {
            Settings = new SettingsProfile
            {
                Settings = settings,
                SelectedMode = input.Settings.SelectedMode,
                SavedOperatingIntent = input.Settings.SavedOperatingIntent
            }
        };
    }

    private static EntryEligibilityInput UpdatePool(
        EntryEligibilityInput input,
        string venue,
        Func<RiskPoolInput, RiskPoolInput> update)
    {
        var pools = input.Pools.ToArray();
        var index = Array.FindIndex(pools, pool => pool.Account.Venue == venue);
        if (index < 0)
            throw new InvalidOperationException($"Fixture venue not found: {venue}");
        pools[index] = update(pools[index]);
        return input with { Pools = pools };
    }

    private static EntryEligibilityInput ReplacePool(EntryEligibilityInput input, RiskPoolInput replacement)
    {
        var pools = input.Pools.ToArray();
        var index = Array.FindIndex(pools, pool => pool.Account.Venue == replacement.Account.Venue);
        if (index < 0)
            throw new InvalidOperationException($"Fixture venue not found: {replacement.Account.Venue}");
        pools[index] = replacement;
        return input with { Pools = pools };
    }

    private static RiskEvidence<T> Evidence<T>(T value, string unit, DateTimeOffset observedAt) =>
        new(value, unit, EntryEligibilityExample.SourceName, observedAt);

    private static T Get<T>(Window window, string name) where T : FrameworkElement =>
        (T)(window.FindName(name) ?? throw new InvalidOperationException($"WPF control not found: {name}"));

    private static void Focus(FrameworkElement element)
    {
        element.BringIntoView();
        PumpDispatcher();
        var focused = Keyboard.Focus(element);
        if (!ReferenceEquals(focused, element))
            throw new InvalidOperationException($"Could not focus WPF control: {element.Name}");
        PumpDispatcher();
    }

    private static void InvokeButton(Button button)
    {
        if (!button.IsVisible || !button.IsEnabled)
            throw new InvalidOperationException($"Cannot invoke hidden or disabled WPF button: {button.Name}");
        var peer = UIElementAutomationPeer.CreatePeerForElement(button)
            ?? throw new InvalidOperationException($"WPF automation peer not found: {button.Name}");
        var invokePattern = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider
            ?? throw new InvalidOperationException($"WPF InvokePattern not available: {button.Name}");
        invokePattern.Invoke();
    }

    private static void RenderWindow(Window window, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(window.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(window.ActualHeight)), 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        if (new FileInfo(path).Length == 0)
            throw new InvalidOperationException($"WPF screenshot is empty: {path}");
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static string ArtifactsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "plans", "007-entry-eligibility.md")))
                return Path.Combine(directory.FullName, "artifacts", "007-entry-eligibility");
        }
        return Path.Combine(AppContext.BaseDirectory, "verification-artifacts", "007-entry-eligibility");
    }
}
