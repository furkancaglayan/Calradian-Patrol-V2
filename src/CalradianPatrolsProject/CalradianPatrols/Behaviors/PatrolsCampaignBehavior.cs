﻿using CalradianPatrols.Base;
using CalradianPatrols.Components;
using CalradianPatrols.Extensions;
using CalradianPatrols.Models;
using CalradianPatrolsV2.CalradianPatrols;
using Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Extensions;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;
using TaleWorlds.SaveSystem;
using Debug = TaleWorlds.Library.Debug;

namespace CalradianPatrols.Behaviors
{
    public partial class PatrolsCampaignBehavior : CampaignBehaviorBase, IPatrolBehaviorInformationProvider
    {
        public PatrolPartyModel Model => CalradianPatrolsModuleManager.Current.PatrolModel;

        public partial class PatrolEncounterData
        {
            [SaveableProperty(1)]
            public CampaignTime EncounterStartTime { get; private set; }

            [SaveableProperty(2)]
            public int DiedInBattle { get; private set; }

            [SaveableProperty(3)]
            public int WoundedInBattle { get; private set; }

            [SaveableProperty(4)]
            public PartyBase EncounteredParty { get; set; }

            [SaveableProperty(5)]
            public Settlement EncounterNearbySettlement { get; set; }

            [SaveableProperty(6)]
            public bool MapEventEnded { get; private set; }

            [SaveableProperty(7)]
            public TroopRoster MemberRosterCopyAtBeginning { get; private set; }

            [SaveableProperty(8)]
            public TroopRoster PrisonerRosterCopyAtBeginning { get; private set; }

            public PatrolEncounterData(MobileParty party, MobileParty encounteredParty, Settlement nearbySettlement)
            {
                EncounteredParty = encounteredParty.Party;
                EncounterStartTime = CampaignTime.Now;
                EncounterNearbySettlement = nearbySettlement;

                MemberRosterCopyAtBeginning = TroopRoster.CreateDummyTroopRoster();
                MemberRosterCopyAtBeginning.Add(party.MemberRoster);

                PrisonerRosterCopyAtBeginning = TroopRoster.CreateDummyTroopRoster();
                PrisonerRosterCopyAtBeginning.Add(party.PrisonRoster);

                MapEventEnded = false;
            }

            public void OnMapEventEnded(int diedInBattle, int woundedInBattle)
            {
                DiedInBattle = diedInBattle;
                WoundedInBattle = woundedInBattle;
                MapEventEnded = true;
            }
        }

        public static bool DisableAi = false;

        private const float MaxDistanceForNearEncounterSettlement = 8f;
        private List<PatrolEncounterData> _currentConversationEncounterDataList = new List<PatrolEncounterData>();
        private Dictionary<Settlement, List<MobileParty>> _patrols = new Dictionary<Settlement, List<MobileParty>>();
        private Dictionary<MobileParty, CampaignTime> _nextDecisionTimes = new Dictionary<MobileParty, CampaignTime>();
        private Dictionary<Settlement, CampaignTime> _spawnQueues = new Dictionary<Settlement, CampaignTime>();
        private Dictionary<Settlement, bool> _autoRecruits = new Dictionary<Settlement, bool>();
        private Dictionary<MobileParty, List<PatrolEncounterData>> _partyEncounters = new Dictionary<MobileParty, List<PatrolEncounterData>>();
        private Dictionary<Clan, int> _clanTiers = new Dictionary<Clan, int>();
        private float _targetWaitHours = Settings.GetInstance().TargetHoursForSpawn, _waitProgressHours = 0f;

        public override void RegisterEvents()
        {
            CampaignEvents.HourlyTickPartyEvent.AddNonSerializedListener(this, PatrolPartyHourlyTick);
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, HourlyTick);
            CampaignEvents.HourlyTickSettlementEvent.AddNonSerializedListener(this, HourlyTickSettlement);
            CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(this, OnMobilePartyDestroyed);
            CampaignEvents.MapEventStarted.AddNonSerializedListener(this, OnMapEventStarted);
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
            CampaignEvents.OnNewGameCreatedPartialFollowUpEndEvent.AddNonSerializedListener(this, OnNewGameCreated);
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunchedEvent);
            CampaignEvents.SettlementEntered.AddNonSerializedListener(this, OnSettlementEntered);
            CampaignEvents.DailyTickSettlementEvent.AddNonSerializedListener(this, DailyTickSettlement);
            CampaignEvents.WeeklyTickEvent.AddNonSerializedListener(this, WeeklyTick);
            CampaignEvents.ClanTierIncrease.AddNonSerializedListener(this, OnPlayerClanTierIncrease);
            CampaignEvents.ConversationEnded.AddNonSerializedListener(this, OnConversationEnded);
            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, OnSettlementOwnerChanged);
            CampaignEvents.TownRebelliosStateChanged.AddNonSerializedListener(this, TownRebelliosStateChanged);
            CampaignEvents.OnGameLoadFinishedEvent.AddNonSerializedListener(this, OnGameLoadFinished);
        }

        private void OnGameLoadFinished()
        {
            if (!_autoRecruits.Any())
            {
                OnNewGameCreated(null);
            }
        }

        private void TownRebelliosStateChanged(Town town, bool rebelliousState)
        {
            if (rebelliousState)
            {
                DisbandUnits(town.Settlement, town.Settlement.OwnerClan);
            }
        }

        private void OnPlayerClanTierIncrease(Clan clan, bool notify)
        {
            if (clan == Clan.PlayerClan && clan.Tier == Model.MinimumTierForPatrolParties && clan.Settlements.Any(x => x.IsTown))
            {
                MBInformationManager.AddQuickInformation(GameTexts.FindText("str_patrol_party_now_available"));
            }
        }

        private void OnSettlementOwnerChanged(Settlement settlement, bool openToClaim, Hero newOwner, Hero previousOwner, Hero capturerHero, ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail detail)
        {
            DisbandUnits(settlement, previousOwner.Clan);
            if (previousOwner == Hero.MainHero && settlement.IsTown)
            {
                _autoRecruits[settlement] = false;
            }
        }

        private void DisbandUnits(Settlement settlement, Clan owner)
        {
            if (_patrols.TryGetValue(settlement, out var parties) && parties != null && parties.Any())
            {
                var goldChange = 0;
                foreach (var party in parties.ToList())
                {
                    var component = (PatrolPartyComponent)party.PartyComponent;
                    component.TakeAction(PatrolPartyComponent.PatrolPartyState.Disbanding);
                    goldChange += HandleExpenses(component);
                    CheckAndRemovePatrolEncounterData(component);
                }

                if (goldChange != 0 && owner != null)
                {
                    GiveGoldAction.ApplyBetweenCharacters(null, owner.Leader, goldChange, true);
                }

                _patrols[settlement].Clear();
            }
        }

        private void OnConversationEnded(IEnumerable<CharacterObject> characters)
        {
            _currentConversationEncounterDataList.Clear();
        }

        private void DailyTickSettlement(Settlement settlement)
        {

        }

        private void HourlyTickSettlement(Settlement settlement)
        {
            if (settlement.IsTown && !settlement.IsUnderSiege && !settlement.InRebelliousState)
            {
                if (settlement.OwnerClan == Clan.PlayerClan)
                {
                    if (_autoRecruits[settlement] && CanHirePatrolParty(settlement, false))
                    {
                        var cost = Model.GetGoldCostForPatrolParty(settlement);
                        AddToSpawnQueue(settlement);
                        GiveGoldAction.ApplyForCharacterToSettlement(settlement.OwnerClan.Leader, settlement, cost, true);
                        var message = GameTexts.FindText("str_patrol_auto_recruit_new");
                        message.SetTextVariable("SETTLEMENT_NAME", settlement.Name);
                        InformationManager.DisplayMessage(new InformationMessage(message.ToString(), new Color(1f, 1f, 1f)));
                    }
                }
                else
                {
                    TrySpawnPartyForSettlement(settlement, false);
                }
            }
        }

        private void TrySpawnPartyForSettlement(Settlement settlement, bool gameStart)
        {
            if (settlement.IsTown && settlement.OwnerClan != Clan.PlayerClan && !settlement.IsUnderSiege && !settlement.InRebelliousState)
            {
                var cost = Model.GetGoldCostForPatrolParty(settlement);

                if (CanHirePatrolParty(settlement, gameStart) && MBRandom.RandomFloat < Model.GetNPCSettlementHirePatrolPartyChance(settlement))
                {
                    if (!gameStart)
                    {
                        AddToSpawnQueue(settlement);
                        GiveGoldAction.ApplyForCharacterToSettlement(settlement.OwnerClan.Leader, settlement, cost, true);
                    }
                    else
                    {
                        CreatePatrolPartyWithTemplate(settlement);
                    }
                }
            }
        }

        private bool CanHirePatrolParty(Settlement settlement, bool isGameStart)
        {
            var (c1, c2) = get_patrol_party_and_spawn_count(settlement);
            return c2 == 0 && c1 + c2 < Model.GetMaxAmountOfPartySizePerSettlement(settlement.OwnerClan, settlement) && (isGameStart || Model.CanNPCClanRecruitPartyForTown(settlement.OwnerClan, settlement.Town));
        }

        private void HourlyTick()
        {
            foreach (var settlement in _spawnQueues.Keys.ToList())
            {
                var spawnQueue = _spawnQueues[settlement];
                if (spawnQueue != null)
                {
                    if (spawnQueue.IsPast && spawnQueue != CampaignTime.Never)
                    {
                        SpawnFromQueue(settlement);
                        _spawnQueues[settlement] = CampaignTime.Never;
                    }
                }
            }
        }

        private void CheckAndRemovePatrolEncounterData(PatrolPartyComponent component)
        {
            if (_partyEncounters.TryGetValue(component.MobileParty, out _))
            {
                _partyEncounters[component.MobileParty].RemoveAll(x => x.EncounterStartTime.ElapsedDaysUntilNow > 3f);
            }
        }

        private void WeeklyTick()
        {
            foreach (var clan in Clan.NonBanditFactions)
            {
                var goldChange = 0;
                foreach (var partyComponent in GetClanPatrolParties(clan))
                {
                    goldChange += HandleExpenses(partyComponent);
                    CheckAndRemovePatrolEncounterData(partyComponent);
                }

                if (goldChange != 0)
                {
                    GiveGoldAction.ApplyBetweenCharacters(null, clan.Leader, goldChange, true);
                    if (clan == Clan.PlayerClan)
                    {
                        var text = goldChange < 0 ?
                                   GameTexts.FindText("str_patrol_party_expenses_paid_text") :
                                   GameTexts.FindText("str_patrol_party_profit_text");

                        text.SetTextVariable("GOLD", Math.Abs(goldChange).ToString());
                        InformationManager.DisplayMessage(new InformationMessage(text.ToString()));
                    }
                }
            }
        }

        private int HandleExpenses(PatrolPartyComponent partyComponent)
        {
            var goldChange = partyComponent.Gold;
            partyComponent.Gold = 0;
            return goldChange;
        }

        private void OnSettlementEntered(MobileParty patrolParty, Settlement settlement, Hero hero)
        {
            if (patrolParty != null && patrolParty.PartyComponent is PatrolPartyComponent component && settlement == component.Settlement)
            {
                SetRestInSettlementAction(patrolParty, component, settlement);
                SellPrisoners(patrolParty, component);
                GatherUnits(patrolParty, component);
                //+1 more because will rest for 1 day
                BuyFoodForNDays(component, settlement, Model.DaysWorthOfFoodToGiveToParty - 1, Model.DaysWorthOfFoodToGiveToParty + 1);
            }
        }

        private void BuyFoodForNDays(PatrolPartyComponent component, Settlement settlement, float daysMin, float daysMax)
        {
            var party = component.MobileParty;
            var dailyFoodConsumption = Math.Abs(party.FoodChange);

            var days = MBRandom.RandomFloatRanged(daysMin, daysMax);

            if (days * dailyFoodConsumption > party.Food)
            {
                var foodRequirement = Math.Ceiling((days * dailyFoodConsumption) - party.Food);

                var cost = 0f;
                var startIndex = MBRandom.RandomInt(0, settlement.ItemRoster.Count);
                for (int i = startIndex; i < settlement.ItemRoster.Count + startIndex && foodRequirement > 0; i++)
                {
                    var currentIndex = i % settlement.ItemRoster.Count;
                    var itemRosterElement = settlement.ItemRoster.GetElementCopyAtIndex(currentIndex);

                    if (!itemRosterElement.IsEmpty &&
                        itemRosterElement.EquipmentElement.Item.IsFood)
                    {
                        var effectiveAmount = (float)Math.Min(itemRosterElement.Amount, foodRequirement);
                        cost += settlement.Town.GetItemPrice(itemRosterElement.EquipmentElement.Item) * effectiveAmount;
                        foodRequirement -= effectiveAmount;
                        party.ItemRoster.AddToCounts(itemRosterElement.EquipmentElement.Item, (int)effectiveAmount);
                    }
                }

                component.Gold -= (int)cost;
            }
        }

        private void SellPrisoners(MobileParty party, PatrolPartyComponent component)
        {
            if (party.PrisonRoster.TotalManCount > 0)
            {
                var gold = 0;
                foreach (var prisoner in party.PrisonRoster.GetTroopRoster())
                {
                    gold += Campaign.Current.Models.RansomValueCalculationModel.PrisonerRansomValue(prisoner.Character);
                }

                SellPrisonersAction.ApplyForAllPrisoners(party, party.PrisonRoster, party.CurrentSettlement, false);
                component.Gold += gold;
            }
        }

        private void GatherUnits(MobileParty party, PatrolPartyComponent component)
        {
            var basicTroop = component.Settlement.Culture.BasicTroop;
            if (basicTroop != null && party.MemberRoster.TotalManCount < Model.Tier1PatrolPartyIdealSize)
            {
                var add = Model.Tier1PatrolPartyIdealSize - party.MemberRoster.TotalManCount;
                party.MemberRoster.AddToCounts(basicTroop, add);
                component.Gold -= Model.GetGoldCostForTroop(basicTroop, party.ActualClan) * add;
                party.MemberRoster.SetLeaderToHigherTroop();
            }
        }

        private void SpawnFromQueue(Settlement settlement)
        {
            var party = CreatePatrolPartyWithTemplate(settlement);

            if (settlement.OwnerClan == Clan.PlayerClan)
            {
                /* Deactived notice - might be too annoying.
                 * var text = GameTexts.FindText("str_new_patrol_party_created");
                text.SetTextVariable("SETTLEMENT", settlement.Name);
                Campaign.Current.CampaignInformationManager.NewMapNoticeAdded(new PatrolPartyCreatedMapNotification(party, text));
                */
            }
        }

        private void AddToSpawnQueue(Settlement settlement)
        {
            var nextTime = CampaignTime.HoursFromNow(Settings.GetInstance().TargetHoursForSpawn);
            _spawnQueues.Set(settlement, nextTime);
        }

        private void OnNewGameCreated(CampaignGameStarter campaignGameStarter)
        {
            foreach (var settlement in Settlement.All)
            {
                if (settlement.IsTown)
                {
                    for (int i = 0; i < 12; i++)
                    {
                        TrySpawnPartyForSettlement(settlement, true);
                    }

                    _autoRecruits.Add(settlement, false);
                }
            }
        }


        private void OnSessionLaunchedEvent(CampaignGameStarter campaignGameStarter)
        {
            AddMenus(campaignGameStarter);
            AddDialogs(campaignGameStarter);
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("_spawnQueues", ref _spawnQueues);
            dataStore.SyncData("_patrols", ref _patrols);
            dataStore.SyncData("_lastDecisionTimes", ref _nextDecisionTimes);
            dataStore.SyncData("_partyEncounters", ref _partyEncounters);
            dataStore.SyncData("_clanTiers", ref _clanTiers);
            dataStore.SyncData("_targetWaitHours", ref _targetWaitHours);
            dataStore.SyncData("_waitProgressHours", ref _waitProgressHours);
            dataStore.SyncData("_autoRecruits", ref _autoRecruits);
        }

        private void OnMapEventStarted(MapEvent mapEvent, PartyBase attackerParty, PartyBase defenderParty)
        {
            AddEncounterDataAtMapEventStart(attackerParty, defenderParty);
            AddEncounterDataAtMapEventStart(defenderParty, attackerParty);
        }

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            if (mapEvent.IsFieldBattle && mapEvent.InvolvedParties.Any(x => x.MobileParty?.PartyComponent is PatrolPartyComponent))
            {
                foreach (var party in mapEvent.AttackerSide.Parties)
                {
                    if (party.Party.IsMobile &&
                        mapEvent.DefenderSide.LeaderParty.IsMobile &&
                        mapEvent.DefenderSide.LeaderParty.MobileParty.IsBandit &&
                        party.Party.MobileParty.PartyComponent is PatrolPartyComponent patrolPartyComponent)
                    {
                        UpdateEncounterDataAtMapEventEnd(patrolPartyComponent);
                        DisableThinkForHours(party.Party.MobileParty, MBRandom.RandomFloatRanged(2, 6));
                    }
                }

                foreach (var party in mapEvent.DefenderSide.Parties)
                {
                    if (party.Party.IsMobile &&
                        mapEvent.AttackerSide.LeaderParty.IsMobile &&
                        mapEvent.AttackerSide.LeaderParty.MobileParty.IsBandit &&
                        party.Party.MobileParty.PartyComponent is PatrolPartyComponent patrolPartyComponent)
                    {
                        UpdateEncounterDataAtMapEventEnd(patrolPartyComponent);
                        DisableThinkForHours(party.Party.MobileParty, MBRandom.RandomFloatRanged(1, 5));
                    }
                }
            }
        }

        private void PatrolPartyHourlyTick(MobileParty party)
        {
            if (DisableAi || party.MapEvent != null || (party.CurrentSettlement != null && party.CurrentSettlement.IsUnderSiege))
            {
                return;
            }

            if (party.PartyComponent is PatrolPartyComponent patrolPartyComponent)
            {
                var goingToSettlement = patrolPartyComponent.State == PatrolPartyComponent.PatrolPartyState.GoingToSettlementForFood ||
                            patrolPartyComponent.State == PatrolPartyComponent.PatrolPartyState.GoingToSettlementForUnits;

                if (!CheckAndDoSettlementActions(party, patrolPartyComponent))
                {
                    if (!goingToSettlement)
                    {
                        if (_nextDecisionTimes[party].IsPast)
                        {
                            MakeNewDecision(party, patrolPartyComponent);
                        }
                        else
                        {
                            //InformationManager.DisplayMessage(new InformationMessage($"Decision cant be made at this time"));
                        }
                    }
                    else
                    {
                        var settlement = patrolPartyComponent.Settlement;
                        if (settlement.IsUnderSiege)
                        {
                            ClearDecision(party, patrolPartyComponent);
                        }
                    }
                }
            }
        }

        private void ClearDecision(MobileParty party, PatrolPartyComponent patrolPartyComponent)
        {
            _nextDecisionTimes[party] = CampaignTime.Now;
        }

        private bool CheckAndDoSettlementActions(MobileParty party, PatrolPartyComponent patrolPartyComponent)
        {
            //TODO:Consider adding emergency solution for food // smth like go to nearest settlement

            var actionTaken = false;
            if (party.CurrentSettlement == patrolPartyComponent.Settlement ||
                patrolPartyComponent.Settlement.IsUnderSiege ||
                patrolPartyComponent.State == PatrolPartyComponent.PatrolPartyState.GoingToSettlementForFood ||
                patrolPartyComponent.State == PatrolPartyComponent.PatrolPartyState.GoingToSettlementForUnits)
            {
                return false;
            }

            if (party.GetRemainingFoodInDays() <= Model.MinimumIdealFoodForDays)
            {
                //Consider sieges?
                SetGoToSettlementToBuyFood(party, patrolPartyComponent, patrolPartyComponent.Settlement);
                actionTaken = true;
            }
            else if (!Model.GetIsRosterStatusGoodForHunting(party))
            {
                /*party.Party.SetAsCameraFollowParty();
                Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;*/
                SetGoToSettlementToGatherUnits(party, patrolPartyComponent, patrolPartyComponent.Settlement);
                actionTaken = true;
            }

            return actionTaken;
        }

        private void MakeNewDecision(MobileParty party, PatrolPartyComponent patrolPartyComponent)
        {
            var searchData = MobileParty.StartFindingLocatablesAroundPosition(party.Position2D, party.SeeingRange);
            var mobileParty = MobileParty.FindNextLocatable(ref searchData);

            MobileParty selectedTarget = null;
            var bestAttackScore = 0f;
            var skipAttack = patrolPartyComponent.State != PatrolPartyComponent.PatrolPartyState.EngagingBandits &&
                             MBRandom.RandomFloat < 0.05f;
            var canChangeTarget = party.TargetParty == null ||
                                  (patrolPartyComponent.State == PatrolPartyComponent.PatrolPartyState.EngagingBandits &&
                                  MBRandom.RandomFloat < 0.8f);

            if (!skipAttack || canChangeTarget)
            {
                while (mobileParty != null)
                {
                    if (mobileParty.IsBandit && mobileParty.IsActive && mobileParty.CurrentSettlement == null)
                    {
                        if (mobileParty.MapEvent != null &&
                            mobileParty.MapEvent.EventType == MapEvent.BattleTypes.FieldBattle &&
                            mobileParty.MapEvent.DefenderSide != mobileParty.MapEventSide &&
                            mobileParty.MapEvent.DefenderSide.LeaderParty != null &&
                            mobileParty.MapEvent.DefenderSide.LeaderParty.IsMobile &&

                            ((mobileParty.MapEvent.DefenderSide.LeaderParty.MobileParty.PartyComponent is
                            PatrolPartyComponent && mobileParty.MapEvent.DefenderSide.LeaderParty.MobileParty.ActualClan == party.ActualClan) ||
                            mobileParty.MapEvent.DefenderSide.LeaderParty.MobileParty.IsVillager ||
                            mobileParty.MapEvent.DefenderSide.LeaderParty.MobileParty.IsCaravan))
                        {
                            selectedTarget = mobileParty;
                            break;
                        }
                        else
                        {
                            var attackScore = Model.GetAttackScoreforBanditParty(party, patrolPartyComponent, mobileParty) + MBRandom.RandomFloatRanged(-0.3f, 0.3f);
                            if (attackScore > bestAttackScore)
                            {
                                selectedTarget = mobileParty;
                                bestAttackScore = attackScore;
                            }
                        }
                    }

                    mobileParty = MobileParty.FindNextLocatable(ref searchData);
                }
            }

            if (Campaign.Current.Models.MapDistanceModel.GetDistance(party, patrolPartyComponent.Settlement) >= 50f)
            {
                SetPatrolAction(party, patrolPartyComponent, patrolPartyComponent.Settlement);
            }
            else if (selectedTarget != null)
            {
                SetEngageBanditsByThink(party, patrolPartyComponent, selectedTarget);
            }
            else if (patrolPartyComponent.State != PatrolPartyComponent.PatrolPartyState.Patrolling)
            {
                var patrolSettlement = patrolPartyComponent.Settlement;
                // sometimes will ptrol villages.
                if (MBRandom.RandomFloat < 0.25f)
                {
                    patrolSettlement = patrolPartyComponent.Settlement.Town.Villages.GetRandomElement().Settlement;
                }

                SetPatrolAction(party, patrolPartyComponent, patrolSettlement);
            }
            else if (patrolPartyComponent.State == PatrolPartyComponent.PatrolPartyState.EngagingBandits &&
                     party.TargetParty != null)
            {
                SetEngageBanditsByThink(party, patrolPartyComponent, party.TargetParty);
            }
        }

        private void OnMobilePartyDestroyed(MobileParty mobileParty, PartyBase destroyerParty)
        {
            if (mobileParty.PartyComponent is PatrolPartyComponent component)
            {
                _nextDecisionTimes.Remove(mobileParty);
                _partyEncounters.Remove(mobileParty);
                if (_patrols.ContainsKey(component.Settlement))
                {
                    _patrols[component.Settlement].Remove(mobileParty);
                }

                if (component.RulerClan == Clan.PlayerClan)
                {
                    if (destroyerParty != null)
                    {
                        var text = GameTexts.FindText("str_patrol_party_destroyed");
                        text.SetTextVariable("SETTLEMENT", component.Settlement.Name);
                        text.SetTextVariable("DESTROYER", destroyerParty.Name);
                        MBInformationManager.AddQuickInformation(text);
                    }
                }
            }
        }

        private void AddEncounterDataAtMapEventStart(PartyBase encounteredParty, PartyBase mainParty)
        {
            if (encounteredParty.IsMobile &&
                mainParty.IsMobile &&
                encounteredParty.MobileParty.IsBandit &&
                mainParty.MobileParty.PartyComponent is PatrolPartyComponent component)
            {
                var nearbySettlement =
                    Helpers.SettlementHelper.FindNearestSettlement(x => Campaign.Current.Models.MapDistanceModel.GetDistance(mainParty.MobileParty, x) < MaxDistanceForNearEncounterSettlement, mainParty.MobileParty);
                AddEncounterDataAtMapEventStartInternal(encounteredParty, component, nearbySettlement);
            }
        }

        private void AddEncounterDataAtMapEventStartInternal(PartyBase encounteredParty, PatrolPartyComponent patrolParty, Settlement nearbySettlement)
        {
            var data = new PatrolEncounterData(patrolParty.MobileParty, encounteredParty.MobileParty, nearbySettlement);
            _partyEncounters[patrolParty.Party.MobileParty].Add(data);
        }

        private void UpdateEncounterDataAtMapEventEnd(PatrolPartyComponent patrolParty)
        {
            if (_partyEncounters.TryGetValue(patrolParty.Party.MobileParty, out var encounters))
            {
                var data = encounters.LastOrDefault(x => !x.MapEventEnded);
                if (data != null)
                {
                    var woundedCount = patrolParty.MobileParty.MemberRoster.TotalWoundedRegulars - data.MemberRosterCopyAtBeginning.TotalWoundedRegulars;
                    var deadCount = data.MemberRosterCopyAtBeginning.TotalManCount - patrolParty.MobileParty.MemberRoster.TotalManCount;
                    data.OnMapEventEnded(deadCount, woundedCount);
                }
            }
        }

        private void SetPatrolAction(MobileParty party, PatrolPartyComponent component, Settlement settlement)
        {
            component.TakeAction(PatrolPartyComponent.PatrolPartyState.Patrolling, settlement);
        }

        private void SetRestInSettlementAction(MobileParty party, PatrolPartyComponent component, Settlement settlement)
        {
            component.TakeAction(PatrolPartyComponent.PatrolPartyState.Resting, settlement);
            DisableThinkForHours(party, MBRandom.RandomFloatRanged(20, 24));
        }

        private void SetEngageBanditsByThink(MobileParty party, PatrolPartyComponent component, MobileParty banditParty)
        {
            component.TakeAction(PatrolPartyComponent.PatrolPartyState.EngagingBandits, party: banditParty);
            DisableThinkForHours(party, 2);
        }

        private void SetGoToSettlementToBuyFood(MobileParty party, PatrolPartyComponent component, Settlement settlement)
        {
            component.TakeAction(PatrolPartyComponent.PatrolPartyState.GoingToSettlementForFood, settlement);
            DisableThinkForHours(party, 36);
        }

        private void SetGoToSettlementToGatherUnits(MobileParty party, PatrolPartyComponent component, Settlement settlement)
        {
            component.TakeAction(PatrolPartyComponent.PatrolPartyState.GoingToSettlementForUnits, settlement);
            DisableThinkForHours(party, 36);
        }

        private void DisableThinkForHours(MobileParty party, float hours)
        {
            _nextDecisionTimes[party] = CampaignTime.HoursFromNow(hours);
        }

        private TextObject GetEncounterText(PatrolEncounterData data)
        {
            var encounterTime = data.EncounterStartTime;
            var encounterSettlement = data.EncounterNearbySettlement;
            var diedInBattle = data.DiedInBattle;
            var woundedInBattle = data.WoundedInBattle;
            var encounterParty = data.EncounteredParty;

            var text = GameTexts.FindText("str_patrol_party_bandit_encounter");
            text.SetTextVariable("BANDIT_NAME", encounterParty.Name);
            text.SetTextVariable("DEAD_COUNT", diedInBattle);
            text.SetTextVariable("WOUNDED_COUNT", woundedInBattle);
            text.SetTextVariable("IS_AROUND_SETTLEMENT", encounterSettlement == null ? 0 : 1);
            if (encounterSettlement != null)
            {
                text.SetTextVariable("SETTLEMENT_NAME", encounterSettlement.EncyclopediaLinkWithName);
            }

            text.SetTextVariable("IS_TODAY", (int)(encounterTime.ElapsedDaysUntilNow) == 0 ? 1 : 0);
            text.SetTextVariable("DAYS_AGO", (int)(encounterTime.ElapsedDaysUntilNow));

            return text;
        }

        private TroopRoster GetRandomTroops(CultureObject culture)
        {
            var roster = TroopRoster.CreateDummyTroopRoster();

            var troopTree = CharacterHelper.GetTroopTree(culture.BasicTroop).ToList();

            // ensure it's around 22-27
            int minTroops = Model.Tier1PatrolPartyIdealSize - 3;
            int maxTroops = Model.Tier1PatrolPartyIdealSize + 3;

            var rand = MBRandom.RandomInt(minTroops, maxTroops);

            // try to get 5 to 10 cavs
            int cavAmount = MBRandom.RandomInt(5, 10);
            var cavTroops = troopTree.Where(x => x.IsMounted).ToList();

            if (cavTroops.Any())
            {
                for (int i = 0; i < cavAmount; i++)
                {
                    var troop = cavTroops.GetRandomElement();
                    roster.AddToCounts(troop, 1);
                }

                rand -= cavAmount;
            }

            //6 - 9 ranged units
            int rangedAmount = MBRandom.RandomInt(6, 9);
            var rangedUnits = troopTree.Where(x => x.IsRanged).ToList();

            if (rangedUnits.Any())
            {
                for (int i = 0; i < rangedAmount; i++)
                {
                    var troop = rangedUnits.GetRandomElement();
                    roster.AddToCounts(troop, 1);
                }

                rand -= rangedAmount;
            }

            var infantry = troopTree.Where(x => x.IsInfantry).ToList();

            if (infantry.Any())
            {
                for (int i = 0; i < rand; i++)
                {
                    var troop = infantry.GetRandomElement();
                    roster.AddToCounts(troop, 1);
                }
            }

            if (roster.TotalManCount < minTroops)
            {
                var num = minTroops - roster.TotalManCount;
                for (int i = 0; i < num; i++)
                {
                    var troop = troopTree.GetRandomElement();
                    roster.AddToCounts(troop, 1);
                }
            }

            return roster;
        }

        private bool EnsureTroopsExists(CultureObject culture, PartyTemplateObject partyTemplate)
        {
            if (partyTemplate == null || culture == null)
            {
                return false;
            }

            var troopTree = CharacterHelper.GetTroopTree(culture.BasicTroop).ToList();
            var troopTreeNoble = CharacterHelper.GetTroopTree(culture.EliteBasicTroop).ToList();

            return partyTemplate.Stacks.TrueForAll(x => troopTree.Contains(x.Character) || troopTreeNoble.Contains(x.Character));
        }

        private MobileParty CreatePatrolPartyWithTemplate(Settlement settlement)
        {
            var template = MBObjectManager.Instance.GetObject<PartyTemplateObject>($"patrol_party_template_tier_1_{settlement.Culture.StringId}");
            var party = PatrolPartyComponent.CreatePatrolParty("patrol_party_1", settlement, settlement.OwnerClan, template);

            if (template != null)
            {
                party.InitializeMobilePartyAroundPosition(template, settlement.GatePosition, 11, 5);
            }
            else
            {
                var roster = GetRandomTroops(settlement.Culture);
                party.InitializeMobilePartyAroundPosition(roster, TroopRoster.CreateDummyTroopRoster(), settlement.GatePosition, 11, 5);
            }


            DisableThinkForHours(party, MBRandom.RandomFloatRanged(2, 7));
            AddPatrolPartyToSettlement(party, settlement);
            GatherUnits(party, party.PartyComponent as PatrolPartyComponent);
            BuyFoodForNDays(party.PartyComponent as PatrolPartyComponent, settlement, Model.DaysWorthOfFoodToGiveToParty - 1, Model.DaysWorthOfFoodToGiveToParty + 1);
            return party;
        }

        private MobileParty CreatePatrolPartyFromGarrison(Settlement settlement, TroopRoster memberRoster)
        {
            var template = MBObjectManager.Instance.GetObject<PartyTemplateObject>($"patrol_party_template_tier_1_{settlement.Culture.Name.ToString().ToLower()}");
            var party = PatrolPartyComponent.CreatePatrolParty("patrol_party_1", settlement, settlement.OwnerClan, template);

            party.InitializeMobilePartyAroundPosition(memberRoster, TroopRoster.CreateDummyTroopRoster(), settlement.GatePosition, 11, 5);

            DisableThinkForHours(party, MBRandom.RandomFloatRanged(2, 7));
            AddPatrolPartyToSettlement(party, settlement);
            BuyFoodForNDays(party.PartyComponent as PatrolPartyComponent, settlement, Model.DaysWorthOfFoodToGiveToParty - 1, Model.DaysWorthOfFoodToGiveToParty + 1);

            return party;
        }

        private void AddPatrolPartyToSettlement(MobileParty party, Settlement settlement)
        {
            Debug.Assert(party.PartyComponent is PatrolPartyComponent, "Party Component is wrong!");
            _patrols.Set(settlement, party);
            _nextDecisionTimes.Set(party, CampaignTime.Now);
            _partyEncounters.Add(party, new List<PatrolEncounterData>());
        }

        public List<PatrolPartyComponent> GetClanPatrolParties(Clan clan)
        {
            var parties = new List<PatrolPartyComponent>();
            foreach (var settlement in _patrols.Keys)
            {
                if (settlement.OwnerClan == clan)
                {
                    parties.AddRange(_patrols[settlement].Select(x => x.PartyComponent as PatrolPartyComponent));
                }
            }

            return parties;
        }
        #region DIALOGS & MENU

        private void AddMenus(CampaignGameStarter campaignGameStarter)
        {
            // Town
            campaignGameStarter.AddGameMenuOption("town", "hire_patrols", GameTexts.FindText("str_hire_menu_title_text").ToString(), game_menu_hire_patrol_menu_on_condition, (MenuCallbackArgs args) => { GameMenu.SwitchToMenu("hire_patrol_option_main_menu"); }, index: 4);

            // Castle
            campaignGameStarter.AddGameMenu("hire_patrol_option_main_menu", "{=!}{MANHUNTER_MENU_TEXT}", town_hire_menu_init);
            campaignGameStarter.AddGameMenuOption("hire_patrol_option_main_menu", "hire_patrols_basic", "{=!}{HIRE_TEXT}", game_menu_town_hire_patrol_basic_on_common_condition, game_menu_town_hire_patrol_basic_on_consequence);
            campaignGameStarter.AddGameMenuOption("hire_patrol_option_main_menu", "hire_patrols_basic", GameTexts.FindText("str_hire_patrol_party_for_gold_garrison").ToString(), game_menu_town_hire_patrol_from_garrison_on_condition, game_menu_town_hire_patrol_from_garrison_on_consequence);
            campaignGameStarter.AddWaitGameMenu("hire_patrol_wait", GameTexts.FindText("str_waiting_party_to_become_ready").ToString(), null, null, hire_patrol_wait_on_consequence, wait_menu_on_tick, GameMenu.MenuAndOptionType.WaitMenuShowOnlyProgressOption, targetWaitHours: _targetWaitHours);
            campaignGameStarter.AddGameMenuOption("hire_patrol_wait", "leave", GameTexts.FindText("str_stop_waiting").ToString(), hire_patrol_wait_leave_on_condition, hire_patrol_wait_on_consequence, true); //is_leave="true"

            campaignGameStarter.AddGameMenuOption("hire_patrol_option_main_menu", "disband_all_units_option", GameTexts.FindText("str_disband_all").ToString(), hire_patrol_disband_on_condition, hire_patrol_disband_on_consequence);
            //auto recruit
            campaignGameStarter.AddGameMenuOption("hire_patrol_option_main_menu", "auto_recruit_option", "{=!}{AUTO_RECRUIT}", hire_patrol_auto_recruit_on_condition, hire_patrol_auto_recruit_on_consequence);
            campaignGameStarter.AddGameMenuOption("hire_patrol_option_main_menu", "hire_patrol_wait_option", GameTexts.FindText("str_wait_until_all_ready").ToString(), game_menu_town_hire_patrol_wait_menu_on_condition, game_menu_town_hire_patrol_wait_menu_on_consequence);

            campaignGameStarter.AddGameMenuOption("hire_patrol_option_main_menu", "hire_patrol_option_main_menu_go_back", GameTexts.FindText("str_back_to_center").ToString(), hire_patrol_option_main_menu_go_back_on_condition, hire_patrol_option_main_menu_go_back_on_consequence, true);
        }

        private void AddDialogs(CampaignGameStarter campaignGameStarter)
        {
            //Start
            campaignGameStarter.AddDialogLine("patrol_greet_player", "start", "patrol_party_player_greeting", "{=!}{GREETING}", patrol_start_talk_on_condition, null);
            campaignGameStarter.AddDialogLine("patrol_greet_player_unpleasant", "patrol_greet_player_unpleasant", "patrol_party_player_greeting", GameTexts.FindText("str_patrol_greet_player_unpleasant").ToString(), null, null);

            //Hostile
            campaignGameStarter.AddPlayerLine("player_threatens_neutral_party", "patrol_party_player_greeting", "patrol_party_threatened", GameTexts.FindText("str_player_threatens_neutral_party").ToString(), conversation_party_is_threated_neutral_on_condition, null);

            campaignGameStarter.AddDialogLine("player_threatens_neutral_party", "patrol_party_threatened", "patrol_party_threatened_answer_1", GameTexts.FindText("str_patrol_party_threatened").ToString(), null, null);
            campaignGameStarter.AddPlayerLine("player_threatens_neutral_party", "patrol_party_threatened_answer_1", "patrol_talk_prefight", GameTexts.FindText("str_patrol_party_threatened_answer_1").ToString(), null, conversation_player_threats_party_verify_on_consequence);
            campaignGameStarter.AddDialogLine("player_threatens_enemy_party_prefight", "patrol_talk_prefight", "close_window", GameTexts.FindText("str_patrol_talk_prefight").ToString(), null, null);
            campaignGameStarter.AddPlayerLine("player_threatens_neutral_party", "patrol_party_threatened_answer_1", "patrol_greet_player_unpleasant", GameTexts.FindText("str_never_mind").ToString(), null, null);

            campaignGameStarter.AddPlayerLine("player_threatens_enemy_party", "patrol_party_player_greeting", "patrol_talk_threaten_on_enemy", GameTexts.FindText("str_patrol_talk_threaten_on_enemy").ToString(), conversation_player_can_attack_party_on_condition, null, clickableConditionDelegate: conversation_player_can_attack_party_on_clickable_condition);
            campaignGameStarter.AddDialogLine("player_threatens_enemy_party", "patrol_talk_threaten_on_enemy", "patrol_talk_threaten_on_enemy_answer1", GameTexts.FindText("str_patrol_talk_threaten_on_enemy_answer1").ToString(), null, null);
            campaignGameStarter.AddPlayerLine("player_threatens_enemy_party", "patrol_talk_threaten_on_enemy_answer1", "patrol_talk_prefight", GameTexts.FindText("str_patrol_talk_prefight").ToString(), null, conversation_player_threats_party_verify_enemy_on_consequence);
            campaignGameStarter.AddPlayerLine("player_threatens_enemy_party", "patrol_talk_threaten_on_enemy_answer1", "patrol_greet_player_unpleasant", GameTexts.FindText("patrol_greet_player_unpleasant").ToString(), null, null);

            //Adventures - 
            campaignGameStarter.AddPlayerLine("player_talk_ask_adventures", "patrol_party_player_greeting", "patrol_talk", GameTexts.FindText("str_patrol_talk_report").ToString(), null, null);
            campaignGameStarter.AddDialogLine("party_answer_adventures", "patrol_talk", "patrol_party_player_greeting", "{=!}{PARTY_ADVENTURES}", patrol_ask_adventures_on_condition, null);

            //Food Situation
            campaignGameStarter.AddPlayerLine("player_talk_ask_about_situtation", "patrol_party_player_greeting", "party_talk_about_food", GameTexts.FindText("party_talk_about_food").ToString(), null, null);
            campaignGameStarter.AddDialogLine("party_talk_about_food", "party_talk_about_food", "party_talk_about_troops_situation", "{=!}{FOOD_SITUATION}", patrol_answer_about_food_on_condition, null);
            campaignGameStarter.AddDialogLine("party_talk_about_troops_situation", "party_talk_about_troops_situation", "patrol_party_player_greeting", "{=!}{TROOP_SITUATION}", patrol_answer_about_troops_on_condition, null);

            //Follow
            campaignGameStarter.AddPlayerLine("player_ask_follow", "patrol_party_player_greeting", "party_follow_main", GameTexts.FindText("str_party_follow_main").ToString(), patrol_ask_party_to_follow_on_condition, null);
            campaignGameStarter.AddPlayerLine("player_ask_stop_follow", "patrol_party_player_greeting", "party_stop_follow_main", GameTexts.FindText("str_party_follow_main_stop").ToString(), patrol_ask_party_to_stop_follow_on_condition, null);
            campaignGameStarter.AddDialogLine("party_follow_main", "party_follow_main", "close_window", "{=!}{FOLLOW_ANSWER}", null, patrol_ask_party_to_follow_on_consequence);
            campaignGameStarter.AddDialogLine("party_stop_follow_main", "party_stop_follow_main", "close_window", GameTexts.FindText("str_party_go").ToString(), null, patrol_ask_party_to_stop_follow_on_consequence);

            //NameChange
            campaignGameStarter.AddPlayerLine("party_change_party_name", "patrol_party_player_greeting", "party_change_party_name_accept", GameTexts.FindText("str_change_party_name").ToString(), patrol_ask_party_name_change_on_condition, null);
            campaignGameStarter.AddDialogLine("party_change_party_name_after", "party_change_party_name_accept", "close_window", GameTexts.FindText("str_sure").ToString(), null, patrol_ask_party_name_change_on_consequence);
            //End
            campaignGameStarter.AddPlayerLine("player_leave", "patrol_party_player_greeting", "close_window", GameTexts.FindText("str_on_my_way").ToString(), null, () => { PlayerEncounter.LeaveEncounter = true; });
        }

        private bool hire_patrol_disband_on_condition(MenuCallbackArgs args)
        {
            args.optionLeaveType = GameMenuOption.LeaveType.Manage;
            args.Tooltip = GameTexts.FindText("str_patrol_disband_all");

            var (c1, c2) = get_patrol_party_and_spawn_count(Settlement.CurrentSettlement);
            if (c1 == 0)
            {
                args.Tooltip = GameTexts.FindText("str_patrol_cant_disband_no_units");
                args.IsEnabled = false;
            }
            else if (c2 > 0)
            {
                args.Tooltip = GameTexts.FindText("str_patrol_cant_disband_queue");
                args.IsEnabled = false;
            }


            return true;
        }

        private void hire_patrol_disband_on_consequence(MenuCallbackArgs args)
        {
            DisbandUnits(Settlement.CurrentSettlement, Settlement.CurrentSettlement.OwnerClan);
            _autoRecruits[Settlement.CurrentSettlement] = false;
            GameMenu.SwitchToMenu("hire_patrol_option_main_menu");
        }

        private bool hire_patrol_auto_recruit_on_condition(MenuCallbackArgs args)
        {
            args.optionLeaveType = GameMenuOption.LeaveType.Recruit;
            var autoRecruitIsOn = _autoRecruits[Settlement.CurrentSettlement];

            if (Settlement.CurrentSettlement.IsUnderSiege)
            {
                args.Tooltip = GameTexts.FindText("str_patrol_auto_recruit_siege_tooltip");
            }

            MBTextManager.SetTextVariable("AUTO_RECRUIT", autoRecruitIsOn ? GameTexts.FindText("str_patrol_auto_recruit_turn_off") : GameTexts.FindText("str_patrol_auto_recruit_turn_on"));
            return true;
        }

        private void hire_patrol_auto_recruit_on_consequence(MenuCallbackArgs args)
        {
            _autoRecruits[Settlement.CurrentSettlement] = !_autoRecruits[Settlement.CurrentSettlement];
            GameMenu.SwitchToMenu("hire_patrol_option_main_menu");
        }

        private bool hire_patrol_wait_leave_on_condition(MenuCallbackArgs args)
        {
            args.optionLeaveType = GameMenuOption.LeaveType.Leave;
            return true;
        }

        private void hire_patrol_wait_on_consequence(MenuCallbackArgs args)
        {
            GameMenu.SwitchToMenu("hire_patrol_option_main_menu");
        }

        private void wait_menu_on_tick(MenuCallbackArgs args, CampaignTime campaignTime)
        {
            _waitProgressHours += (float)campaignTime.ToHours;
            args.MenuContext.GameMenu.SetProgressOfWaitingInMenu(_waitProgressHours / (_targetWaitHours + 1));
        }

        private bool game_menu_town_hire_patrol_wait_menu_on_condition(MenuCallbackArgs args)
        {
            args.optionLeaveType = GameMenuOption.LeaveType.Wait;
            return _spawnQueues.TryGetValue(Settlement.CurrentSettlement, out var time) && time.IsFuture && time != CampaignTime.Never;
        }

        private void game_menu_town_hire_patrol_wait_menu_on_consequence(MenuCallbackArgs args)
        {
            GameMenu.SwitchToMenu("hire_patrol_wait");
        }
        private void conversation_player_threats_party_verify_on_consequence()
        {
            var enemyParty = PlayerEncounter.EncounteredMobileParty;
            if (enemyParty != null)
            {
                if (!FactionManager.IsAtWarAgainstFaction(Hero.MainHero.MapFaction, enemyParty.MapFaction) && Hero.MainHero.MapFaction.Leader == Hero.MainHero)
                {
                    ChangeRelationAction.ApplyPlayerRelation(enemyParty.MapFaction.Leader, -10);
                    DeclareWarAction.ApplyByPlayerHostility(Hero.MainHero.MapFaction, enemyParty.MapFaction);
                }
            }
        }

        private void conversation_player_threats_party_verify_enemy_on_consequence()
        {
            var enemyParty = PlayerEncounter.EncounteredMobileParty;
            if (enemyParty != null)
            {
                ChangeRelationAction.ApplyPlayerRelation(enemyParty.MapFaction.Leader, -5);
            }
        }

        private bool conversation_party_is_threated_neutral_on_condition()
        {
            return PlayerEncounter.EncounteredMobileParty?.PartyComponent != null &&
                   Settlement.CurrentSettlement == null &&
                   PlayerEncounter.EncounteredMobileParty.PartyComponent is PatrolPartyComponent partyComponent &&
                   FactionManager.IsNeutralWithFaction(MobileParty.ConversationParty.MapFaction, Hero.MainHero.MapFaction) &&
                   PlayerEncounter.EncounteredMobileParty != null;
        }

        private bool conversation_player_can_attack_party_on_condition()
        {
            return PlayerEncounter.EncounteredMobileParty?.PartyComponent != null &&
                   PlayerEncounter.EncounteredMobileParty.PartyComponent is PatrolPartyComponent partyComponent &&
                   FactionManager.IsAtWarAgainstFaction(MobileParty.ConversationParty.MapFaction, Hero.MainHero.MapFaction) &&
                   PlayerEncounter.EncounteredMobileParty != null;
        }

        private bool conversation_player_can_attack_party_on_clickable_condition(out TextObject explanation)
        {
            var party = PlayerEncounter.EncounteredMobileParty;

            if (party != null && party.MapFaction != null)
            {
                if (party.MapFaction.NotAttackableByPlayerUntilTime.IsFuture)
                {
                    explanation = GameTexts.FindText("str_enemy_not_attackable_tooltip");
                    return false;
                }
            }

            explanation = TextObject.Empty;
            return true;
        }

        private bool patrol_answer_about_troops_on_condition()
        {
            if (MobileParty.ConversationParty != null && MobileParty.ConversationParty.PartyComponent is PatrolPartyComponent)
            {
                var banditPrisonerCount = MobileParty.ConversationParty.PrisonRoster.TotalManCount;

                TextObject prisonerStatus;

                if (banditPrisonerCount > 1)
                {
                    prisonerStatus = GameTexts.FindText("str_patrol_party_caught_bandits_text");
                }
                else
                {
                    prisonerStatus = GameTexts.FindText("str_patrol_party_didnt_caught_bandits_text");
                }

                var component = MobileParty.ConversationParty.PartyComponent as PatrolPartyComponent;

                MBTextManager.SetTextVariable("TROOP_SITUATION", prisonerStatus);
                MBTextManager.SetTextVariable("SETTLEMENT", component.Settlement.Name);
                return true;
            }

            return false;
        }

        private bool patrol_ask_party_to_follow_on_condition()
        {
            if (MobileParty.ConversationParty != null && MobileParty.ConversationParty.PartyComponent is PatrolPartyComponent component)
            {
                return component.RulerClan == Clan.PlayerClan && !component.IsFollowingPlayer;
            }

            return false;
        }

        private bool patrol_ask_party_name_change_on_condition()
        {
            if (MobileParty.ConversationParty != null && MobileParty.ConversationParty.PartyComponent is PatrolPartyComponent component)
            {
                return component.RulerClan == Clan.PlayerClan;
            }

            return false;
        }

        private void patrol_ask_party_name_change_on_consequence()
        {
            if (MobileParty.ConversationParty != null && MobileParty.ConversationParty.PartyComponent is PatrolPartyComponent component)
            {
                OpenNameChangeInquiry();
            }

            if (PlayerEncounter.Current != null)
            {
                PlayerEncounter.LeaveEncounter = true;
            }
        }

        private bool patrol_ask_party_to_stop_follow_on_condition()
        {
            if (MobileParty.ConversationParty != null && MobileParty.ConversationParty.PartyComponent is PatrolPartyComponent component)
            {
                return component.RulerClan == Clan.PlayerClan && component.IsFollowingPlayer;
            }

            return false;
        }

        private bool patrol_answer_about_food_on_condition()
        {
            if (MobileParty.ConversationParty != null && MobileParty.ConversationParty.PartyComponent is PatrolPartyComponent)
            {
                var foodForDays = MobileParty.ConversationParty.GetRemainingFoodInDays();
                TextObject text;

                if (foodForDays <= 1)
                {
                    text = GameTexts.FindText("str_patrol_party_out_of_food_text");

                }
                else if (foodForDays < 3f)
                {
                    text = GameTexts.FindText("str_patrol_party_low_on_food_text");
                }
                else
                {
                    text = GameTexts.FindText("str_patrol_party_has_food_text");
                }

                text.SetTextVariable("DAYS", (int)(foodForDays));
                var component = (PatrolPartyComponent)MobileParty.ConversationParty.PartyComponent;
                text.SetTextVariable("SETTLEMENT", component.Settlement.Name);
                MBTextManager.SetTextVariable("FOOD_SITUATION", text);
                return true;
            }

            return false;
        }

        private void patrol_ask_party_to_stop_follow_on_consequence()
        {
            if (MobileParty.ConversationParty != null && MobileParty.ConversationParty.PartyComponent is PatrolPartyComponent component)
            {
                ClearDecision(MobileParty.ConversationParty, component);
                PlayerEncounter.LeaveEncounter = true;
            }
        }

        private void patrol_ask_party_to_follow_on_consequence()
        {
            //Lead the way.
            TextObject text;
            var component = MobileParty.ConversationParty.PartyComponent as PatrolPartyComponent;
            if (component.State == PatrolPartyComponent.PatrolPartyState.GoingToSettlementForFood)
            {
                text = GameTexts.FindText("str_patrol_party_out_of_food_cant_follow_text");
            }
            else if (component.State == PatrolPartyComponent.PatrolPartyState.GoingToSettlementForUnits)
            {
                text = GameTexts.FindText("str_patrol_party_out_of_men_cant_follow_text");
            }
            else
            {
                text = GameTexts.FindText("str_patrol_party_can_follow_player_text");
                component.TakeAction(PatrolPartyComponent.PatrolPartyState.FollowingLord, party: MobileParty.MainParty);
                DisableThinkForHours(MobileParty.ConversationParty, 12);
            }

            MBTextManager.SetTextVariable("FOLLOW_ANSWER", text);
            PlayerEncounter.LeaveEncounter = true;
        }

        private bool game_menu_hire_patrol_menu_on_condition(MenuCallbackArgs args)
        {
            args.optionLeaveType = GameMenuOption.LeaveType.Submenu;

            if (Settlement.CurrentSettlement.OwnerClan != Clan.PlayerClan)
            {
                args.Tooltip = GameTexts.FindText("str_hire_menu_settlement_not_owned_text");
                args.IsEnabled = false;
            }
            else if (Clan.PlayerClan.Tier < Model.MinimumTierForPatrolParties)
            {
                args.Tooltip = GameTexts.FindText("str_hire_menu_not_enough_renown_text");
                args.IsEnabled = false;
            }

            return true;
        }

        private bool game_menu_town_hire_patrol_basic_on_common_condition(MenuCallbackArgs args)
        {
            args.optionLeaveType = GameMenuOption.LeaveType.Recruit;
            var (c1, c2) = get_patrol_party_and_spawn_count(Settlement.CurrentSettlement);

            var cost = Model.GetGoldCostForPatrolParty(Settlement.CurrentSettlement);

            if (Settlement.CurrentSettlement.IsUnderSiege)
            {
                args.Tooltip = GameTexts.FindText("str_hire_menu_settlement_besieged");
                args.IsEnabled = false;
            }
            else if (Model.GetMaxAmountOfPartySizePerSettlement(Settlement.CurrentSettlement.OwnerClan, Settlement.CurrentSettlement) <= c1 + c2)
            {
                args.Tooltip = GameTexts.FindText("str_hire_menu_has_max_amount_of_parties_text");
                args.IsEnabled = false;
            }
            else if (Hero.MainHero.Gold < cost)
            {
                args.Tooltip = GameTexts.FindText("str_hire_menu_not_enough_gold_text");
                args.IsEnabled = false;
            }
            else if (_spawnQueues.TryGetValue(Settlement.CurrentSettlement, out var time) && time != CampaignTime.Never)
            {
                args.Tooltip = GameTexts.FindText("str_hire_menu_already_has_party_on_queue_text");
                args.IsEnabled = false;
            }

            var text = GameTexts.FindText("str_hire_patrol_party_for_gold");
            text.SetTextVariable("GOLD", cost);
            text.SetTextVariable("GOLD_ICON", HyperlinkTexts.GoldIcon);
            MBTextManager.SetTextVariable("HIRE_TEXT", text);

            return true;
        }

        private bool game_menu_town_hire_patrol_from_garrison_on_condition(MenuCallbackArgs args)
        {
            args.optionLeaveType = GameMenuOption.LeaveType.Recruit;

            if (Settlement.CurrentSettlement.Town?.GarrisonParty != null &&
                Settlement.CurrentSettlement.Town.GarrisonParty.MemberRoster?.TotalHealthyCount > 0)
            {
                return game_menu_town_hire_patrol_basic_on_common_condition(args);
            }
            else
            {
                args.Tooltip = GameTexts.FindText("str_hire_menu_no_garrison");
                MBTextManager.SetTextVariable("SETTLEMENT", Settlement.CurrentSettlement.Name);
                args.IsEnabled = false;
            }


            return true;
        }

        private void game_menu_town_hire_patrol_from_garrison_on_consequence(MenuCallbackArgs args)
        {
            TroopRoster leftMemberRoster = TroopRoster.CreateDummyTroopRoster();
            TroopRoster leftPrisonerRoster = TroopRoster.CreateDummyTroopRoster();
            TroopRoster rightMemberRoster = TroopRoster.CreateDummyTroopRoster();
            TroopRoster rightPrisonerRoster = TroopRoster.CreateDummyTroopRoster();

            leftMemberRoster.Add(Settlement.CurrentSettlement.Town.GarrisonParty.MemberRoster);

            var leftPartyName = Settlement.CurrentSettlement.Town.GarrisonParty.Name;
            var rightPartyName = GameTexts.FindText("str_settlement_town_patrol");
            rightPartyName.SetTextVariable("SETTLEMENT", Settlement.CurrentSettlement.Name);

            PartyScreenManager.OpenScreenWithDummyRoster(
               leftMemberRoster,
               leftPrisonerRoster,
               rightMemberRoster,
               rightPrisonerRoster,
               leftPartyName,
               rightPartyName,
               leftMemberRoster.TotalManCount,
               Model.MaximumCustomGarrisonPartySize,
               PartyScreenDoneCondition,
               OnPartyScreenClosed,
               null);
        }

        private Tuple<bool, TextObject> PartyScreenDoneCondition(TroopRoster leftMemberRoster, TroopRoster leftPrisonRoster, TroopRoster rightMemberRoster, TroopRoster rightPrisonRoster, int leftLimitNum, int rightLimitNum)
        {
            if (rightMemberRoster.TotalManCount > Model.MaximumCustomGarrisonPartySize)
            {
                MBTextManager.SetTextVariable("MAX_PARTY_SIZE", Model.MaximumCustomGarrisonPartySize);
                return new Tuple<bool, TextObject>(false, GameTexts.FindText("str_patrol_name_max_size"));
            }
            else if (rightMemberRoster.TotalManCount < Model.MinimumCustomGarrisonPartySize)
            {
                MBTextManager.SetTextVariable("MIN_PARTY_SIZE", Model.MinimumCustomGarrisonPartySize);
                return new Tuple<bool, TextObject>(false, GameTexts.FindText("str_patrol_name_min_size"));
            }

            return new Tuple<bool, TextObject>(true, TextObject.Empty);
        }

        private void OnPartyScreenClosed(PartyBase leftOwnerParty, TroopRoster leftMemberRoster, TroopRoster leftPrisonRoster, PartyBase rightOwnerParty, TroopRoster rightMemberRoster, TroopRoster rightPrisonRoster, bool fromCancel)
        {
            if (!fromCancel && rightMemberRoster.TotalManCount > 0) //Apply changes
            {
                var party = CreatePatrolPartyFromGarrison(Settlement.CurrentSettlement, rightMemberRoster);
                GiveGoldAction.ApplyForCharacterToSettlement(Settlement.CurrentSettlement.OwnerClan.Leader, Settlement.CurrentSettlement, Model.GetGoldCostForPatrolParty(Settlement.CurrentSettlement));

                foreach (var troop in rightMemberRoster.GetTroopRoster())
                {
                    Settlement.CurrentSettlement.Town.GarrisonParty.MemberRoster.AddToCounts(troop.Character, -troop.Number, false, -troop.WoundedNumber, -troop.Xp);
                }

                OpenNameChangeInquiry();
            }
        }

        private void OpenNameChangeInquiry()
        {
            if (MobileParty.ConversationParty != null)
            {
                var patrolPartyComponent = MobileParty.ConversationParty.PartyComponent as PatrolPartyComponent;
                if (patrolPartyComponent != null)
                {
                    var titleText = GameTexts.FindText("str_select_party_name");
                    var defaultText = GameTexts.FindText("str_settlement_town_patrol");
                    defaultText.SetTextVariable("SETTLEMENT", patrolPartyComponent.Settlement.Name);

                    TextInquiryData changeClanNameInquiry = new TextInquiryData(
                            titleText.ToString(),
                            string.Empty,
                            true,
                            false,
                            GameTexts.FindText("str_done").ToString(),
                            null,
                            NamingPartyIsDone,
                            null,
                            false,
                            IsPartyNameApplicable,
                            defaultInputText: defaultText.ToString());

                    InformationManager.ShowTextInquiry(changeClanNameInquiry);
                }
            }

        }

        private void NamingPartyIsDone(string name)
        {
            if (MobileParty.ConversationParty != null)
            {
                var patrolPartyComponent = MobileParty.ConversationParty.PartyComponent as PatrolPartyComponent;
                if (patrolPartyComponent != null)
                {
                    var partyName = new TextObject("{=!}{CUSTOM_PARTY_NAME}");
                    partyName.SetTextVariable("CUSTOM_PARTY_NAME", name);
                    patrolPartyComponent.SetCustomName(partyName);
                    patrolPartyComponent.MobileParty.Party.SetAsCameraFollowParty();
                }
            }
        }

        private static Tuple<bool, string> IsPartyNameApplicable(string name)
        {
            var text = TextObject.Empty;
            var result = true;
            bool isValidLength = name.Length >= 5 && name.Length <= 50;
            if (!isValidLength)
            {
                result = false;
                text = GameTexts.FindText("str_invalid_name", "0");
            }
            else if (Common.TextContainsSpecialCharacters(name))
            {
                result = false;
                text = GameTexts.FindText("str_invalid_name", "1");
            }
            else if (name.StartsWith(" ") || name.EndsWith(" "))
            {
                result = false;
                text = GameTexts.FindText("str_invalid_name", "2");
            }
            else if (name.Contains("  "))
            {
                result = false;
                text = GameTexts.FindText("str_invalid_name", "3");
            }

            return new Tuple<bool, string>(result, text.ToString());
        }

        private void game_menu_town_hire_patrol_basic_on_consequence(MenuCallbackArgs args)
        {
            GiveGoldAction.ApplyForCharacterToSettlement(Hero.MainHero, Settlement.CurrentSettlement, Model.GetGoldCostForPatrolParty(Settlement.CurrentSettlement));
            AddToSpawnQueue(Settlement.CurrentSettlement);

            var text = GameTexts.FindText("str_hire_menu_hire_party_notification_text");
            MBInformationManager.AddQuickInformation(text);
            GameMenu.SwitchToMenu("hire_patrol_option_main_menu");
        }

        private void game_menu_town_hire_patrol_cheat_on_consequence(MenuCallbackArgs args)
        {
            CreatePartiesForSettlementCheat(Settlement.CurrentSettlement, 3);
            GameMenu.SwitchToMenu("hire_patrol_option_main_menu");
        }

        private void CreatePartiesForSettlementCheat(Settlement settlement, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _ = CreatePatrolPartyWithTemplate(settlement);
            }
        }

        private bool patrol_ask_adventures_on_condition()
        {
            if (MobileParty.ConversationParty != null && MobileParty.ConversationParty.PartyComponent is PatrolPartyComponent component)
            {
                var encounterList = _partyEncounters[MobileParty.ConversationParty].Where(x => !_currentConversationEncounterDataList.Contains(x));

                if (component.RulerClan != Clan.PlayerClan)
                {
                    MBTextManager.SetTextVariable("PARTY_ADVENTURES", GameTexts.FindText("str_patrol_party_report_response_negative"));
                }
                else
                {
                    if (encounterList.IsEmpty())
                    {
                        if (_partyEncounters[MobileParty.ConversationParty].Any())
                        {
                            MBTextManager.SetTextVariable("PARTY_ADVENTURES", GameTexts.FindText("str_patrol_party_report_response_nothing_else"));
                        }
                        else
                        {
                            MBTextManager.SetTextVariable("PARTY_ADVENTURES", GameTexts.FindText("str_patrol_party_report_response_nothing"));
                        }
                    }
                    else
                    {
                        var selected = encounterList.GetRandomElementInefficiently();
                        _currentConversationEncounterDataList.Add(selected);

                        MBTextManager.SetTextVariable("PARTY_ADVENTURES", GetEncounterText(selected));
                    }
                }

                return true;
            }

            return false;
        }

        private bool patrol_start_talk_on_condition()
        {
            if (MobileParty.ConversationParty != null && MobileParty.ConversationParty.PartyComponent is PatrolPartyComponent patrolPartyComponent)
            {
                TextObject greet;
                if (patrolPartyComponent.RulerClan == Clan.PlayerClan)
                {
                    greet = GameTexts.FindText("str_patrol_party_greet_ruler");
                }
                else if (patrolPartyComponent.RulerClan.Kingdom == Clan.PlayerClan.Kingdom)
                {
                    greet = GameTexts.FindText("str_patrol_party_greet_friendly_lord");
                }
                else if (!patrolPartyComponent.RulerClan.MapFaction.IsAtWarWith(Clan.PlayerClan.MapFaction))
                {
                    greet = GameTexts.FindText("str_patrol_party_greet_neutral");
                }
                else
                {
                    greet = GameTexts.FindText("str_patrol_party_greet_enemy");
                }

                greet.SetCharacterProperties("PLAYER", Hero.MainHero.CharacterObject);
                greet.SetTextVariable("SETTLEMENT", patrolPartyComponent.Settlement.Name);
                MBTextManager.SetTextVariable("GREETING", greet.ToString());

                return true;
            }

            return false;
        }

        [GameMenuInitializationHandler("hire_patrol_wait")]
        [GameMenuInitializationHandler("hire_patrol_option_main_menu")]
        private static void game_menu_hire_init_general(MenuCallbackArgs args)
        {
            args.MenuContext.SetBackgroundMeshName(Settlement.CurrentSettlement.SettlementComponent.WaitMeshName);
        }

        private void town_hire_menu_init(MenuCallbackArgs args)
        {
            var text = GameTexts.FindText("str_hire_menu_description_text");
            text.SetTextVariable("SETTLEMENT", Settlement.CurrentSettlement.EncyclopediaLinkWithName);
            text.SetTextVariable("NEWLINE", "\n");

            var (c1, c2) = get_patrol_party_and_spawn_count(Settlement.CurrentSettlement);

            var status = GameTexts.FindText("str_hire_menu_title_settlement_party_status");
            status.SetTextVariable("CURRENT_ACTIVE_COUNT", c1);
            status.SetTextVariable("SETTLEMENT", Settlement.CurrentSettlement.EncyclopediaLinkWithName);
            status.SetTextVariable("CURRENT_QUEUE_COUNT", c2);
            status.SetTextVariable("NEWLINE", "\n");
            text.SetTextVariable("CURRENT_PATROL_STATUS", status);

            MBTextManager.SetTextVariable("MANHUNTER_MENU_TEXT", text);
            args.MenuTitle = GameTexts.FindText("str_hire_menu_title_text");

            if (c2 > 0)
            {
                _targetWaitHours = -_spawnQueues[Settlement.CurrentSettlement].ElapsedHoursUntilNow;
                _waitProgressHours = 0f;
            }
        }

        private bool hire_patrol_option_main_menu_go_back_on_condition(MenuCallbackArgs args)
        {
            args.optionLeaveType = GameMenuOption.LeaveType.Leave;
            return true;
        }

        private void hire_patrol_option_main_menu_go_back_on_consequence(MenuCallbackArgs args)
        {
            args.optionLeaveType = GameMenuOption.LeaveType.Leave;
            if (Settlement.CurrentSettlement.IsTown)
            {
                GameMenu.SwitchToMenu("town");
            }
        }

        private (int, int) get_patrol_party_and_spawn_count(Settlement settlement)
        {
            _patrols.TryGetValue(settlement, out var parties);
            if (!_spawnQueues.TryGetValue(settlement, out var partiesSpawnTimes))
            {
                return (parties?.Count ?? 0, 0);
            }

            return (parties?.Count ?? 0, partiesSpawnTimes == CampaignTime.Never ? 0 : 1);
        }
        #endregion
        #region Interface

        public int GetActivePatrolPartyCount(Settlement settlement)
        {
            var (c1, _) = get_patrol_party_and_spawn_count(settlement);
            return c1;
        }

        public int GetPatrolPartyOnQueueCount(Settlement settlement)
        {
            var (_, c2) = get_patrol_party_and_spawn_count(settlement);
            return c2;
        }

        #endregion
        #region CHEATS
        [CommandLineFunctionality.CommandLineArgumentFunction("send_everyone_home", "manhunters")]
        public static string SendEveryoneHome(List<string> strings)
        {
            var behavior = Campaign.Current.GetCampaignBehavior<PatrolsCampaignBehavior>();
            foreach (var patrolList in behavior._patrols)
            {
                foreach (var patrolParty in patrolList.Value)
                {
                    var component = patrolParty.PartyComponent as PatrolPartyComponent;


                    if (patrolParty.CurrentSettlement == null)
                    {
                        behavior.SetGoToSettlementToBuyFood(patrolParty, component, component.Settlement);
                    }
                    else
                    {
                        EnterSettlementAction.ApplyForParty(patrolParty, patrolParty.CurrentSettlement);
                    }
                }
            }

            return "OK";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("set_parties_visible", "manhunters")]
        public static string MakePartiesVisible(List<string> strings)
        {
            var behavior = Campaign.Current.GetCampaignBehavior<PatrolsCampaignBehavior>();
            foreach (var patrolList in behavior._patrols)
            {
                foreach (var patrolParty in patrolList.Value)
                {
                    patrolParty.IsVisible = true;
                }
            }

            return "OK";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("print_party_counts", "manhunters")]
        public static string PrintPartyCounts(List<string> strings)
        {
            var msg = string.Empty;
            var behavior = Campaign.Current.GetCampaignBehavior<PatrolsCampaignBehavior>();
            foreach (var town in Settlement.All)
            {
                if (town.IsTown)
                {
                    var (a, b) = behavior.get_patrol_party_and_spawn_count(town);
                    if (a + b > 0)
                    {
                        msg += $"{town.Name} --- Active: {a} <>  Queue: {b} \n";
                    }
                }
            }

            return msg;
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("fast_start", "manhunters")]
        public static string FastStart(List<string> strings)
        {
            CampaignCheats.AddRenown(new List<string>() { "1000000" });
            CampaignCheats.AddGoldToAllHeroes(new List<string>() { "10000000" });

            foreach (var settlementName in new string[] { "Marunath", "Seonon", "Pen Cannoc", "Varcheg", "Lageta", "Rovalt", "Galend", "Epicrotea", "Sibir", "Makeb", "Chaikand" })
            {
                var settlement = CampaignCheats.GetSettlement(settlementName);
                CampaignCheats.GiveSettlementToPlayer(new List<string>() { settlementName });
                Campaign.Current.GetCampaignBehavior<PatrolsCampaignBehavior>()._autoRecruits[settlement] = true;
            }

            return "OK";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("spawn_patrol_parties", "manhunters")]
        public static string SpawnPatrolParties(List<string> strings)
        {
            var behavior = Campaign.Current.GetCampaignBehavior<PatrolsCampaignBehavior>();
            for (int i = 0; i < 5; i++)
            {
                var party = behavior.CreatePatrolPartyWithTemplate(Town.AllTowns.GetRandomElement().Settlement);
                party.Position2D = Helpers.MobilePartyHelper.FindReachablePointAroundPosition(MobileParty.MainParty.Position2D, 3, 1);
                behavior.DisableThinkForHours(party, 25);
            }

            return "OK";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("remove_all", "manhunters")]
        public static string RemoveAll(List<string> strings)
        {
            var behavior = Campaign.Current.GetCampaignBehavior<PatrolsCampaignBehavior>();
            if (behavior != null)
            {
                behavior._autoRecruits.Clear();
                behavior._clanTiers.Clear();
                behavior._nextDecisionTimes.Clear();
                behavior._spawnQueues.Clear();
                behavior._partyEncounters.Clear();
                behavior._currentConversationEncounterDataList.Clear();

                foreach (var settlement in behavior._patrols.Keys)
                {
                    for (int i = behavior._patrols[settlement].Count - 1; i >= 0; i--)
                    {
                        var party = behavior._patrols[settlement][i];
                        DestroyPartyAction.Apply(null, party);
                    }
                }

                behavior._patrols.Clear();
            }
            return "OK";
        }
        [CommandLineFunctionality.CommandLineArgumentFunction("spawn_patrol_parties_for_settlement", "manhunters")]
        public static string SpawnPatrolPartiesForSettlement(List<string> strings)
        {
            var settlement = CampaignCheats.GetSettlement(CampaignCheats.ConcatenateString(strings));
            if (settlement == null)
            {
                return CampaignCheats.SettlementNotFound;
            }

            var behavior = Campaign.Current.GetCampaignBehavior<PatrolsCampaignBehavior>();
            for (int i = 0; i < 5; i++)
            {
                var party = behavior.CreatePatrolPartyWithTemplate(settlement);
                behavior.DisableThinkForHours(party, 15);
            }

            return "OK";
        }

        private static string get_message(Dictionary<Clan, int> dict, string midText)
        {
            var txt = "";
            foreach (var clan in dict.Keys)
            {
                txt += $"{clan.Name}, {midText}: {dict[clan]}\n";
            }

            return $"--------------------\n{txt}--------------------\n";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("add_xp_to_all_parties", "manhunters")]
        public static string AddXpToAllParties(List<string> strings)
        {
            var parties = Campaign.Current.GetCampaignBehavior<PatrolsCampaignBehavior>()._patrols;

            foreach (var settlement in parties.Keys)
            {
                foreach (var party in parties[settlement])
                {
                    for (int i = 0; i < party.MemberRoster.Count; i++)
                    {
                        party.MemberRoster.AddXpToTroopAtIndex(10000, i);
                    }
                }
            }

            return $"OK";

        }

        [CommandLineFunctionality.CommandLineArgumentFunction("toggle_ai", "manhunters")]
        public static string DisableAI(List<string> strings)
        {
            PatrolsCampaignBehavior.DisableAi = !PatrolsCampaignBehavior.DisableAi;
            var txt = PatrolsCampaignBehavior.DisableAi ? "disabled" : "enabled";
            return $"Ai is {txt}";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("spawn_bandits_around", "manhunters")]
        public static string SpawnBanditPartiesAround(List<string> strings)
        {
            foreach (var banditClan in Clan.BanditFactions)
            {
                var randomHideout = Helpers.SettlementHelper.FindNearestSettlement(x => x.IsHideout && x.OwnerClan == banditClan);
                var party = BanditPartyComponent.CreateBanditParty("bandit_cheat_party_1", banditClan, randomHideout.Hideout, false);

                TroopRoster memberRoster = new TroopRoster(party.Party);
                CharacterObject troop = banditClan.BasicTroop;

                memberRoster.AddToCounts(troop, MBRandom.RandomInt(6, 14));

                TroopRoster prisonerRoster = new TroopRoster(party.Party);

                party.InitializeMobilePartyAroundPosition(
                    memberRoster,
                    prisonerRoster,
                    MobileParty.MainParty.Position2D,
                    10,
                    5);
            }

            return "OK";
        }
    }
    #endregion
}
