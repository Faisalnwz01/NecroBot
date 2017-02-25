﻿#region using directives

using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PoGo.NecroBot.Logic.Common;
using PoGo.NecroBot.Logic.Event;
using PoGo.NecroBot.Logic.Exceptions;
using PoGo.NecroBot.Logic.Logging;
using PoGo.NecroBot.Logic.Model;
using PoGo.NecroBot.Logic.Model.Settings;
using PoGo.NecroBot.Logic.PoGoUtils;
using PoGo.NecroBot.Logic.State;
using PoGo.NecroBot.Logic.Utils;
using PokemonGo.RocketAPI.Enums;
using POGOProtos.Data;
using POGOProtos.Inventory.Item;
using POGOProtos.Map.Fort;
using POGOProtos.Map.Pokemon;
using POGOProtos.Networking.Responses;
using TinyIoC;
using POGOProtos.Enums;
using System.Collections.Generic;
using PoGo.NecroBot.Logic;
#endregion

namespace PoGo.NecroBot.Logic.Tasks
{
    public static class CatchPokemonTask
    {
        public static Dictionary<ItemId, int> AmountOfBerries;
        private static Random Random => new Random((int)DateTime.Now.Ticks);

        // Structure of calling Tasks

        // ## From CatchNearbyPokemonTask
        // await CatchPokemonTask.Execute(session, cancellationToken, encounter, pokemon, currentFortData: null, sessionAllowTransfer:sessionAllowTransfer);

        // ## From CatchLurePokemonTask
        // await CatchPokemonTask.Execute(session, cancellationToken, encounter, pokemon, currentFortData, sessionAllowTransfer: true);

        // ## From CatchIncensePokemonTask
        // await CatchPokemonTask.Execute(session, cancellationToken, encounter, pokemon, currentFortData: null, sessionAllowTransfer: true);

        // ## From SnipePokemonTask
        // await CatchPokemonTask.Execute(session, cancellationToken, encounter, pokemon, currentFortData: null, sessionAllowTransfer: true);

        // ## From MSniperServiceTask
        // await CatchPokemonTask.Execute(session, cancellationToken, encounter, pokemon, currentFortData: null, sessionAllowTransfer: true);

        private static int CatchFleeContinuouslyCount = 0;

        /// <summary>
        /// Because this function sometime being called inside loop, return true it mean we don't want break look, false it mean not need to call this , break a loop from caller function 
        /// </summary>
        /// <param name="session"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="encounter"></param>
        /// <param name="pokemon"></param>
        /// <param name="currentFortData"></param>
        /// <param name="sessionAllowTransfer"></param>
        /// <returns></returns>
        public static async Task<bool> Execute(ISession session,
            CancellationToken cancellationToken,
            dynamic encounter,
            MapPokemon pokemon,
            FortData currentFortData,
            bool sessionAllowTransfer)
        {
            TinyIoC.TinyIoCContainer.Current.Resolve<MultiAccountManager>().ThrowIfSwitchAccountRequested();
            // If the encounter is null nothing will work below, so exit now
            if (encounter == null) return true;
            // Exit if user defined max limits reached
            if (session.Stats.CatchThresholdExceeds(session))
            {
                if (session.LogicSettings.AllowMultipleBot &&
                    session.LogicSettings.MultipleBotConfig.SwitchOnCatchLimit &&
                        TinyIoCContainer.Current.Resolve<MultiAccountManager>().AllowSwitch())
                {
                    throw new ActiveSwitchByRuleException()
                    {
                        MatchedRule = SwitchRules.CatchLimitReached,
                        ReachedValue = session.LogicSettings.CatchPokemonLimit
                    };
                }

                return false;
            }
            using (var block = new BlockableScope(session, BotActions.Catch))
            {
                if (!await block.WaitToRun()) return true;

                AmountOfBerries = new Dictionary<ItemId, int>();

                cancellationToken.ThrowIfCancellationRequested();

                float probability = encounter.CaptureProbability?.CaptureProbability_[0];

                PokemonData encounteredPokemon;
                long unixTimeStamp;
                ulong _encounterId;
                string _spawnPointId;

                // Calling from CatchNearbyPokemonTask and SnipePokemonTask
                if (encounter is EncounterResponse &&
                    (encounter?.Status == EncounterResponse.Types.Status.EncounterSuccess))
                {
                    encounteredPokemon = encounter.WildPokemon?.PokemonData;
                    unixTimeStamp = encounter.WildPokemon?.LastModifiedTimestampMs
                                    + encounter.WildPokemon?.TimeTillHiddenMs;
                    _spawnPointId = encounter.WildPokemon?.SpawnPointId;
                    _encounterId = encounter.WildPokemon?.EncounterId;
                }
                // Calling from CatchIncensePokemonTask
                else if (encounter is IncenseEncounterResponse &&
                         (encounter?.Result == IncenseEncounterResponse.Types.Result.IncenseEncounterSuccess))
                {
                    encounteredPokemon = encounter?.PokemonData;
                    unixTimeStamp = pokemon.ExpirationTimestampMs;
                    _spawnPointId = pokemon.SpawnPointId;
                    _encounterId = pokemon.EncounterId;
                }
                // Calling from CatchLurePokemon
                else if (encounter is DiskEncounterResponse &&
                         encounter?.Result == DiskEncounterResponse.Types.Result.Success &&
                         !(currentFortData == null))
                {
                    encounteredPokemon = encounter?.PokemonData;
                    unixTimeStamp = currentFortData.LureInfo.LureExpiresTimestampMs;
                    _spawnPointId = currentFortData.Id;
                    _encounterId = currentFortData.LureInfo.EncounterId;
                }
                else return true; // No success to work with, exit

                // Check for pokeballs before proceeding
                var pokeball = GetBestBall(session, encounteredPokemon, probability);
                if (pokeball == ItemId.ItemUnknown)
                {
                    Logger.Write(session.Translation.GetTranslation(TranslationString.ZeroPokeballInv));
                    return false;
                }

                // Calculate CP and IV
                var pokemonCp = encounteredPokemon?.Cp;
                var pokemonIv = PokemonInfo.CalculatePokemonPerfection(encounteredPokemon);
                var lv = PokemonInfo.GetLevel(encounteredPokemon);

                // Calculate distance away
                var latitude = encounter is EncounterResponse || encounter is IncenseEncounterResponse
                    ? pokemon.Latitude
                    : currentFortData.Latitude;
                var longitude = encounter is EncounterResponse || encounter is IncenseEncounterResponse
                    ? pokemon.Longitude
                    : currentFortData.Longitude;

                var distance = LocationUtils.CalculateDistanceInMeters(session.Client.CurrentLatitude,
                    session.Client.CurrentLongitude, latitude, longitude);
                if (session.LogicSettings.ActivateMSniper)
                {
                    var newdata = new MSniperServiceTask.EncounterInfo();
                    newdata.EncounterId = _encounterId.ToString();
                    newdata.Iv = Math.Round(pokemonIv, 2);
                    newdata.Latitude = latitude.ToString("G17", CultureInfo.InvariantCulture);
                    newdata.Longitude = longitude.ToString("G17", CultureInfo.InvariantCulture);
                    newdata.PokemonId = (int)(encounteredPokemon?.PokemonId ?? 0);
                    newdata.PokemonName = encounteredPokemon?.PokemonId.ToString();
                    newdata.SpawnPointId = _spawnPointId;
                    newdata.Move1 = PokemonInfo.GetPokemonMove1(encounteredPokemon).ToString();
                    newdata.Move2 = PokemonInfo.GetPokemonMove2(encounteredPokemon).ToString();
                    newdata.Expiration = unixTimeStamp;

                    session.EventDispatcher.Send(newdata);
                }

                DateTime expiredDate =
                    new DateTime(1970, 1, 1, 0, 0, 0).AddMilliseconds(Convert.ToDouble(unixTimeStamp));
                var encounterEV = new EncounteredEvent()
                {
                    Latitude = latitude,
                    Longitude = longitude,
                    PokemonId = encounteredPokemon.PokemonId,
                    IV = pokemonIv,
                    Level = (int)lv,
                    Expires = expiredDate.ToUniversalTime(),
                    ExpireTimestamp = unixTimeStamp,
                    SpawnPointId = _spawnPointId,
                    EncounterId = _encounterId.ToString(),
                    Move1 = PokemonInfo.GetPokemonMove1(encounteredPokemon).ToString(),
                    Move2 = PokemonInfo.GetPokemonMove2(encounteredPokemon).ToString(),
                };

                //add catch to avoid snipe duplicate
                string uniqueCacheKey =
                    $"{session.Settings.PtcUsername}{session.Settings.GoogleUsername}{Math.Round(encounterEV.Latitude, 6)}{(int)encounterEV.PokemonId}{Math.Round(encounterEV.Longitude, 6)}";
                session.Cache.Add(uniqueCacheKey, encounterEV, DateTime.Now.AddMinutes(30));

                session.EventDispatcher.Send(encounterEV);

                if (IsNotMetWithCatchCriteria(session, encounteredPokemon, pokemonIv, lv, pokemonCp))
                {
                    session.EventDispatcher.Send(new NoticeEvent
                    {
                        Message = session.Translation.GetTranslation(TranslationString.PokemonSkipped,
                            encounteredPokemon.PokemonId)
                    });
                    session.Cache.Add(_encounterId.ToString(), encounteredPokemon, expiredDate);
                    Logger.Write(
                        $"Filter catch not met. {encounteredPokemon.PokemonId.ToString()} IV {pokemonIv} lv {lv} {pokemonCp} move1 {PokemonInfo.GetPokemonMove1(encounteredPokemon)} move 2 {PokemonInfo.GetPokemonMove2(encounteredPokemon)}");
                    return true;
                }

                CatchPokemonResponse caughtPokemonResponse = null;
                var lastThrow = CatchPokemonResponse.Types.CatchStatus.CatchSuccess; // Initializing lastThrow
                var attemptCounter = 1;

                // Main CatchPokemon-loop
                do
                {
                    if (session.LogicSettings.UseHumanlikeDelays)
                    {
                        DelayingUtils.Delay(session.LogicSettings.BeforeCatchDelay, 0);
                    }

                    if ((session.LogicSettings.MaxPokeballsPerPokemon > 0 &&
                         attemptCounter > session.LogicSettings.MaxPokeballsPerPokemon))
                        break;

                    pokeball = GetBestBall(session, encounteredPokemon, probability);
                    if (pokeball == ItemId.ItemUnknown)
                    {
                        session.EventDispatcher.Send(new NoPokeballEvent
                        {
                            Id = encounter is EncounterResponse ? pokemon.PokemonId : encounter?.PokemonData.PokemonId,
                            Cp = encounteredPokemon.Cp
                        });
                        return false;
                    }

                    // Determine whether to use berries or not
                    if (lastThrow != CatchPokemonResponse.Types.CatchStatus.CatchMissed)
                    {
                        //AmountOfBerries++;
                        //if (AmountOfBerries <= session.LogicSettings.MaxBerriesToUsePerPokemon)
                        await UseBerry(session, 
                            encounterEV.PokemonId, 
                            _encounterId, 
                            _spawnPointId,
                            pokemonIv,
                            pokemonCp.HasValue? pokemonCp.Value: 10000,  //unknow CP pokemon, want to use berry
                            encounterEV.Level,
                            probability,
                            cancellationToken);
                    }

                    bool hitPokemon = true;

                    //default to excellent throw
                    var normalizedRecticleSize = 1.95;

                    //default spin
                    var spinModifier = 1.0;

                    //Humanized throws
                    if (session.LogicSettings.EnableHumanizedThrows)
                    {
                        //thresholds: https://gist.github.com/anonymous/077d6dea82d58b8febde54ae9729b1bf
                        var spinTxt = "Curve";
                        var hitTxt = "Excellent";
                        if (pokemonCp > session.LogicSettings.ForceExcellentThrowOverCp ||
                            pokemonIv > session.LogicSettings.ForceExcellentThrowOverIv)
                        {
                            normalizedRecticleSize = Random.NextDouble() * (1.95 - 1.7) + 1.7;
                        }
                        else if (pokemonCp >= session.LogicSettings.ForceGreatThrowOverCp ||
                                 pokemonIv >= session.LogicSettings.ForceGreatThrowOverIv)
                        {
                            normalizedRecticleSize = Random.NextDouble() * (1.95 - 1.3) + 1.3;
                            hitTxt = "Great";
                        }
                        else
                        {
                            var regularThrow = 100 - (session.LogicSettings.ExcellentThrowChance +
                                                      session.LogicSettings.GreatThrowChance +
                                                      session.LogicSettings.NiceThrowChance);
                            var rnd = Random.Next(1, 101);

                            if (rnd <= regularThrow)
                            {
                                normalizedRecticleSize = Random.NextDouble() * (1 - 0.1) + 0.1;
                                hitTxt = "Ordinary";
                            }
                            else if (rnd <= regularThrow + session.LogicSettings.NiceThrowChance)
                            {
                                normalizedRecticleSize = Random.NextDouble() * (1.3 - 1) + 1;
                                hitTxt = "Nice";
                            }
                            else if (rnd <=
                                     regularThrow + session.LogicSettings.NiceThrowChance +
                                     session.LogicSettings.GreatThrowChance)
                            {
                                normalizedRecticleSize = Random.NextDouble() * (1.7 - 1.3) + 1.3;
                                hitTxt = "Great";
                            }

                            if (Random.NextDouble() * 100 > session.LogicSettings.CurveThrowChance)
                            {
                                spinModifier = 0.0;
                                spinTxt = "Straight";
                            }
                        }

                        // Round to 2 decimals
                        normalizedRecticleSize = Math.Round(normalizedRecticleSize, 2);

                        // Missed throw check
                        int missChance = Random.Next(1, 101);
                        if (missChance <= session.LogicSettings.ThrowMissPercentage &&
                            session.LogicSettings.EnableMissedThrows)
                        {
                            hitPokemon = false;
                        }

                        Logger.Write($"(Threw ball) {hitTxt} throw, {spinTxt}-ball, HitPokemon = {hitPokemon}...",
                            LogLevel.Debug);
                    }

                    caughtPokemonResponse =
                        await session.Client.Encounter.CatchPokemon(
                            encounter is EncounterResponse || encounter is IncenseEncounterResponse
                                ? pokemon.EncounterId
                                : _encounterId,
                            encounter is EncounterResponse || encounter is IncenseEncounterResponse
                                ? pokemon.SpawnPointId
                                : currentFortData.Id, pokeball, normalizedRecticleSize, spinModifier, hitPokemon);


                    await session.Inventory.UpdateInventoryItem(pokeball);

                    var evt = new PokemonCaptureEvent()
                    {
                        Status = caughtPokemonResponse.Status,
                        CaptureReason = caughtPokemonResponse.CaptureReason,
                        Latitude = latitude,
                        Longitude = longitude
                    };

                    lastThrow = caughtPokemonResponse.Status; // sets lastThrow status


                    if (caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchSuccess)
                    {
                        var totalExp = 0;
                        var totalStarDust = caughtPokemonResponse.CaptureAward.Stardust.Sum();
                        if (encounteredPokemon != null)
                        {
                            encounteredPokemon.Id = caughtPokemonResponse.CapturedPokemonId;
                        }
                        foreach (var xp in caughtPokemonResponse.CaptureAward.Xp)
                        {
                            totalExp += xp;
                        }
                        var stardust = session.Inventory.UpdateStarDust(totalStarDust);

                        evt.Exp = totalExp;
                        evt.Stardust = stardust;
                        evt.UniqueId = caughtPokemonResponse.CapturedPokemonId;
                        evt.Candy = session.Inventory.GetCandyFamily(pokemon.PokemonId);

                        if (session.LogicSettings.UseCatchLimit)
                        {
                            session.Stats.AddPokemonTimestamp(DateTime.Now.Ticks);
                            session.EventDispatcher.Send(new CatchLimitUpdate(session.Stats.GetNumPokemonsInLast24Hours(), session.LogicSettings.CatchPokemonLimit));
                        }
                    }

                    evt.CatchType = encounter is EncounterResponse
                        ? session.Translation.GetTranslation(TranslationString.CatchTypeNormal)
                        : encounter is DiskEncounterResponse
                            ? session.Translation.GetTranslation(TranslationString.CatchTypeLure)
                            : session.Translation.GetTranslation(TranslationString.CatchTypeIncense);
                    evt.CatchTypeText = encounter is EncounterResponse
                        ? "normal"
                        : encounter is DiskEncounterResponse
                            ? "lure"
                            : "incense";
                    evt.Id = encounter is EncounterResponse
                        ? pokemon.PokemonId
                        : encounter?.PokemonData.PokemonId;
                    evt.EncounterId = _encounterId;
                    evt.Move1 = PokemonInfo.GetPokemonMove1(encounteredPokemon);
                    evt.Move2 = PokemonInfo.GetPokemonMove2(encounteredPokemon);
                    evt.Expires = pokemon?.ExpirationTimestampMs ?? 0;
                    evt.SpawnPointId = _spawnPointId;
                    evt.Level = PokemonInfo.GetLevel(encounteredPokemon);
                    evt.Cp = encounteredPokemon.Cp;
                    evt.MaxCp = PokemonInfo.CalculateMaxCp(encounteredPokemon.PokemonId);
                    evt.Perfection = Math.Round(PokemonInfo.CalculatePokemonPerfection(encounteredPokemon));
                    evt.Probability = Math.Round(probability * 100, 2);
                    evt.Distance = distance;
                    evt.Pokeball = pokeball;
                    evt.Attempt = attemptCounter;

                    //await session.Inventory.RefreshCachedInventory();

                    evt.BallAmount = session.Inventory.GetItemAmountByType(pokeball);
                    evt.Rarity = PokemonGradeHelper.GetPokemonGrade(evt.Id).ToString();

                    session.EventDispatcher.Send(evt);

                    attemptCounter++;

                    // If Humanlike delays are used
                    if (session.LogicSettings.UseHumanlikeDelays)
                    {
                        switch (caughtPokemonResponse.Status)
                        {
                            case CatchPokemonResponse.Types.CatchStatus.CatchError:
                                DelayingUtils.Delay(session.LogicSettings.CatchErrorDelay, 0);
                                break;

                            case CatchPokemonResponse.Types.CatchStatus.CatchSuccess:
                                DelayingUtils.Delay(session.LogicSettings.CatchSuccessDelay, 0);
                                break;

                            case CatchPokemonResponse.Types.CatchStatus.CatchEscape:
                                DelayingUtils.Delay(session.LogicSettings.CatchEscapeDelay, 0);
                                break;

                            case CatchPokemonResponse.Types.CatchStatus.CatchFlee:
                                DelayingUtils.Delay(session.LogicSettings.CatchFleeDelay, 0);
                                break;

                            case CatchPokemonResponse.Types.CatchStatus.CatchMissed:
                                DelayingUtils.Delay(session.LogicSettings.CatchMissedDelay, 0);
                                break;

                            default:
                                break;
                        }
                    }
                    else DelayingUtils.Delay(session.LogicSettings.DelayBetweenPlayerActions, 0);
                } while (caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchMissed ||
                         caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchEscape);

                if (session.LogicSettings.AllowMultipleBot)
                {
                    if (caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchFlee)
                    {
                        CatchFleeContinuouslyCount++;
                        if (CatchFleeContinuouslyCount > session.LogicSettings.MultipleBotConfig.CatchFleeCount &&
                            TinyIoCContainer.Current.Resolve<MultiAccountManager>().AllowSwitch())
                        {
                            CatchFleeContinuouslyCount = 0;

                            throw new ActiveSwitchByRuleException()
                            {
                                MatchedRule = SwitchRules.CatchFlee,
                                ReachedValue = session.LogicSettings.MultipleBotConfig.CatchFleeCount
                            };
                        }
                    }
                    else
                    {
                        //reset if not catch flee.
                        CatchFleeContinuouslyCount = 0;
                        MSniperServiceTask.UnblockSnipe();
                    }
                }

                session.Actions.RemoveAll(x => x == BotActions.Catch);

                if (MultipleBotConfig.IsMultiBotActive(session.LogicSettings))
                    ExecuteSwitcher(session, encounterEV, uniqueCacheKey);

                if (session.LogicSettings.TransferDuplicatePokemonOnCapture &&
                    session.LogicSettings.TransferDuplicatePokemon &&
                    sessionAllowTransfer &&
                    caughtPokemonResponse != null &&
                    caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchSuccess)
                {
                    if (session.LogicSettings.UseNearActionRandom)
                        await HumanRandomActionTask.TransferRandom(session, cancellationToken);
                    else
                        await TransferDuplicatePokemonTask.Execute(session, cancellationToken);
                }
            }
            return true;
        }

        private static void ExecuteSwitcher(ISession session, EncounteredEvent encounterEV, string cacheKey)
        {
            //if distance is very far. that is snip pokemon
            var accountManager = TinyIoCContainer.Current.Resolve<MultiAccountManager>();

            var curentkey = session.Settings.AuthType == AuthType.Google
                  ? session.Settings.GoogleUsername
                  : session.Settings.PtcUsername;

            curentkey += encounterEV.EncounterId;
            session.Cache.Add(curentkey, encounterEV, DateTime.Now.AddMinutes(15));

            var evalNextBot = accountManager.FindAvailableAccountForPokemonSwitch(encounterEV.EncounterId);
            if (evalNextBot == null) return;
            if (session.Stats.IsSnipping
                //assume that all pokemon catch from 250+m is snipe
                || LocationUtils.CalculateDistanceInMeters(
                    encounterEV.Latitude,
                    encounterEV.Longitude,
                    session.Client.CurrentLatitude,
                    session.Client.CurrentLongitude
                ) > 1000)
            {

                var snipePokemonFiler = session.LogicSettings.PokemonSnipeFilters.GetFilter<SnipeFilter>(encounterEV.PokemonId);

                if (session.LogicSettings.PokemonSnipeFilters.ContainsKey(encounterEV.PokemonId))
                {
                    var filter = session.LogicSettings.PokemonSnipeFilters[encounterEV.PokemonId];
                    if (session.LogicSettings.AllowMultipleBot &&
                        filter.AllowMultiAccountSnipe &&
                        filter.IsMatch(encounterEV.IV,
                        (PokemonMove)Enum.Parse(typeof(PokemonMove), encounterEV.Move1),
                        (PokemonMove)Enum.Parse(typeof(PokemonMove), encounterEV.Move2),
                        encounterEV.Level, true))
                    {
                        //thow
                        throw new ActiveSwitchByPokemonException()
                        {
                            EncounterData = encounterEV,
                            LastLatitude = encounterEV.Latitude,
                            LastLongitude = encounterEV.Longitude,
                            LastEncounterPokemonId = encounterEV.PokemonId,
                            Snipe = true,
                            Bot = evalNextBot
                        };

                    }
                }

                return;

            }

            if (MultipleBotConfig.IsMultiBotActive(session.LogicSettings) &&
                session.LogicSettings.MultipleBotConfig.OnRarePokemon &&
                (
                    session.LogicSettings.MultipleBotConfig.MinIVToSwitch < encounterEV.IV ||
                    (
                        session.LogicSettings.BotSwitchPokemonFilters.ContainsKey(encounterEV.PokemonId) &&
                        (
                            session.LogicSettings.BotSwitchPokemonFilters[encounterEV.PokemonId].IV < encounterEV.IV ||
                            (session.LogicSettings.BotSwitchPokemonFilters[encounterEV.PokemonId].LV > 0 && session
                                 .LogicSettings.BotSwitchPokemonFilters[encounterEV.PokemonId]
                                 .LV < encounterEV.Level)
                        )
                    )
                ))
            {



                if (evalNextBot != null)
                {
                    //cancel all running task.
                    session.CancellationTokenSource.Cancel();
                    throw new ActiveSwitchByPokemonException()
                    {
                        LastLatitude = encounterEV.Latitude,
                        LastLongitude = encounterEV.Longitude,
                        LastEncounterPokemonId = encounterEV.PokemonId,
                        Bot = evalNextBot
                    };
                }
            }
        }

        private static bool IsNotMetWithCatchCriteria(ISession session, PokemonData encounteredPokemon,
            double pokemonIv, double lv, int? cp)
        {
            if (session.LogicSettings.UsePokemonToNotCatchFilter &&
                session.LogicSettings.PokemonsNotToCatch.Contains(encounteredPokemon.PokemonId)) return true;
            if (session.LogicSettings.UseTransferFilterToCatch &&
                session.LogicSettings.PokemonsTransferFilter.ContainsKey(encounteredPokemon.PokemonId))
            {
                var filter = session.LogicSettings.PokemonsTransferFilter[encounteredPokemon.PokemonId];
                if (filter != null && filter.CatchOnlyPokemonMeetTransferCriteria)
                {
                    var move1 = PokemonInfo.GetPokemonMove1(encounteredPokemon).ToString();
                    var move2 = PokemonInfo.GetPokemonMove2(encounteredPokemon).ToString();

                    if (filter.MovesOperator == "or" &&
                        (filter.Moves.Count > 0 &&
                         filter.Moves.Any(x => x[0] == encounteredPokemon.Move1 && x[1] == encounteredPokemon.Move2)))
                    {
                        return true; //he has the moves we don't meed.
                    }

                    if (filter.KeepMinOperator == "and"
                        && ((cp.HasValue && cp.Value < filter.KeepMinCp)
                            || pokemonIv < filter.KeepMinIvPercentage
                            || (filter.UseKeepMinLvl && lv < filter.KeepMinLvl))
                        && (
                            filter.Moves.Count == 0 ||
                            filter.Moves.Any(x => x[0] == encounteredPokemon.Move1 && x[1] == encounteredPokemon.Move2)
                        ))
                    {
                        return true; //not catch pokemon
                    }

                    if (filter.KeepMinOperator == "or" && ((!cp.HasValue || cp < filter.KeepMinCp)
                                                           && pokemonIv < filter.KeepMinIvPercentage
                                                           && (!filter.UseKeepMinLvl || lv < filter.KeepMinLvl))
                        && (
                            filter.Moves.Count == 0 ||
                            filter.Moves.Any(x => x[0] == encounteredPokemon.Move1 && x[1] == encounteredPokemon.Move2)
                        ))
                    {
                        return true; //not catch pokemon
                    }
                }
            }
            return false;
        }

        public static ItemId GetBestBall(ISession session, PokemonData encounteredPokemon,
            float probability)
        {
            var pokemonCp = encounteredPokemon.Cp;
            var pokemonId = encounteredPokemon.PokemonId;
            var iV = Math.Round(PokemonInfo.CalculatePokemonPerfection(encounteredPokemon), 2);
            var pokeBallsCount = session.Inventory.GetItemAmountByType(ItemId.ItemPokeBall);
            var greatBallsCount = session.Inventory.GetItemAmountByType(ItemId.ItemGreatBall);
            var ultraBallsCount = session.Inventory.GetItemAmountByType(ItemId.ItemUltraBall);
            var masterBallsCount = session.Inventory.GetItemAmountByType(ItemId.ItemMasterBall);

            if (masterBallsCount > 0 && (
                         session.LogicSettings.UseBallOperator.BoolFunc(
                              pokemonCp >= session.LogicSettings.UseMasterBallAboveCp,
                              probability < session.LogicSettings.UseMasterBallBelowCatchProbability
                            )
                        || session.LogicSettings.PokemonToUseMasterball.Contains(pokemonId)))
                return ItemId.ItemMasterBall;


            if (ultraBallsCount > 0 &&
                session.LogicSettings.UseBallOperator.BoolFunc(pokemonCp >= session.LogicSettings.UseUltraBallAboveCp,
                                                               iV >= session.LogicSettings.UseUltraBallAboveIv,
                                                               probability < session.LogicSettings.UseUltraBallBelowCatchProbability))
                return ItemId.ItemUltraBall;

            if (greatBallsCount > 0 && session.LogicSettings.UseBallOperator.BoolFunc(pokemonCp >= session.LogicSettings.UseGreatBallAboveCp,
                                        iV >= session.LogicSettings.UseGreatBallAboveIv,
                                        probability < session.LogicSettings.UseGreatBallBelowCatchProbability)
                                        )
                return ItemId.ItemGreatBall;

            if (pokeBallsCount > 0)
                return ItemId.ItemPokeBall;
            if (greatBallsCount > 0)
                return ItemId.ItemGreatBall;
            if (ultraBallsCount > 0)
                return ItemId.ItemUltraBall;
            if (masterBallsCount > 0 && !session.LogicSettings.PokemonToUseMasterball.Any())
                return ItemId.ItemMasterBall;

            return ItemId.ItemUnknown;
        }

        public static async Task UseBerry(ISession session,
            PokemonId pokemonId,
            ulong encounterId,
            string spawnPointId,
            double pokemonIv,
            float pokemonCp,
            float pokemonLv,
            float probability,
            CancellationToken cancellationToken)
        {

            var itemToUses = session.LogicSettings.ItemUseFilters;


            foreach (var item in itemToUses)
            {
                var inventoryItems = session.Inventory.GetItems();

                var berries = inventoryItems.Where(p => p.ItemId == item.Key);
                var berry = berries.FirstOrDefault();

                if (berry == null || berry.Count <= 0)
                    continue;
                var filter = item.Value;

                var itemRecycleFilter = session.LogicSettings.ItemRecycleFilter.FirstOrDefault(x => x.Key == item.Key);
                
                if ( (filter.UseIfExceedBagRecycleFilter && 
                    !itemRecycleFilter.Equals(new KeyValuePair<ItemId, int>()) && 
                    itemRecycleFilter.Value< berry.Count)
                    || ((filter.Pokemons.Count == 0 || filter.Pokemons.Contains(pokemonId)) &&
                    (!AmountOfBerries.ContainsKey(item.Key) || AmountOfBerries[item.Key] < filter.MaxItemsUsePerPokemon) &&
                    filter.Operator.BoolFunc(
                          pokemonIv >= filter.UseItemMinIV,
                          pokemonCp >= filter.UseItemMinCP,
                          probability < filter.CatchProbability,
                          pokemonLv >= filter.UseItemMinLevel)))
                {
                    var useCaptureItem = await session.Client.Encounter.UseItemEncounter(encounterId, item.Key, spawnPointId);
                    //berry.Count -= 1;
                    if (useCaptureItem.Status == UseItemEncounterResponse.Types.Status.Success)
                    {
                        if(!AmountOfBerries.ContainsKey(item.Key))
                        {
                            AmountOfBerries.Add(item.Key, 0);
                        }
                        AmountOfBerries[item.Key] = AmountOfBerries[item.Key] + 1;
                        session.EventDispatcher.Send(new UseBerryEvent { BerryType = item.Key, Count = berry.Count - 1 });
                        await session.Inventory.UpdateInventoryItem(berry.ItemId);
                        break;//cant only use 1 berries at 1
                    }
                    else
                    {
                        Logger.Debug($"Use berries result : {useCaptureItem.Status}");
                    }
                }
                await DelayingUtils.DelayAsync(session.LogicSettings.DelayBetweenPlayerActions, 500, cancellationToken);
            }

        }

    }
}