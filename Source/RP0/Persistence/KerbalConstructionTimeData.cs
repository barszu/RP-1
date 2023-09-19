﻿using System;
using System.Reflection;
using System.Collections.Generic;
using UniLinq;
using UnityEngine;
using RP0.DataTypes;
using ToolbarControl_NS;

namespace RP0
{
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, new GameScenes[] { GameScenes.EDITOR, GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION })]
    public class KerbalConstructionTimeData : ScenarioModule
    {
        #region Statics
        public static bool TechListIgnoreUpdates = false;
        public static bool VesselErrorAlerted = false;

        internal const string _modId = "KCT_NS";
        internal const string _modName = "Kerbal Construction Time";
        public static ToolbarControl ToolbarControl;
        #endregion


        [KSPField(isPersistant = true)]
        public bool enabledForSave = HighLogic.CurrentGame.Mode == Game.Modes.CAREER ||
                                     HighLogic.CurrentGame.Mode == Game.Modes.SCIENCE_SANDBOX ||
                                     HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX;

        [KSPField(isPersistant = true)] public float SciPointsTotal = -1f;
        [KSPField(isPersistant = true)] public bool IsSimulatedFlight = false;
        [KSPField(isPersistant = true)] public bool ExperimentalPartsEnabled = true;
        [KSPField(isPersistant = true)] public bool DisableFailuresInSim = true;
        [KSPField(isPersistant = true)] public int Researchers = 0;
        [KSPField(isPersistant = true)] public int Applicants = 0;
        [KSPField(isPersistant = true)] public bool StarterLCBuilding = false;
        [KSPField(isPersistant = true)] public bool HiredStarterApplicants = false;
        [KSPField(isPersistant = true)] public bool StartedProgram = false;
        [KSPField(isPersistant = true)] public bool AcceptedContract = false;
        public bool FirstRunNotComplete => !(StarterLCBuilding && HiredStarterApplicants && StartedProgram && AcceptedContract);

        public const int VERSION = 4;
        [KSPField(isPersistant = true)] public int LoadedSaveVersion = VERSION;

        [KSPField(isPersistant = true)] public bool IsFirstStart = true;

        [KSPField(isPersistant = true)] public SimulationParams SimulationParams = new SimulationParams();


        [KSPField(isPersistant = true)]
        private PersistentList<LCEfficiency> _lcEfficiencies = new PersistentList<LCEfficiency>();
        public PersistentList<LCEfficiency> LCEfficiencies => _lcEfficiencies;
        public Dictionary<LaunchComplex, LCEfficiency> LCToEfficiency = new Dictionary<LaunchComplex, LCEfficiency>();

        private readonly Dictionary<Guid, LaunchComplex> _LCIDtoLC = new Dictionary<Guid, LaunchComplex>();
        public LaunchComplex LC(Guid id) => _LCIDtoLC.TryGetValue(id, out var lc) ? lc : null;
        private readonly Dictionary<Guid, LCLaunchPad> _LPIDtoLP = new Dictionary<Guid, LCLaunchPad>();
        public LCLaunchPad LP(Guid id) => _LPIDtoLP[id];

        [KSPField(isPersistant = true)]
        public PersistentObservableList<ResearchProject> TechList = new PersistentObservableList<ResearchProject>();

        [KSPField(isPersistant = true)]
        public PersistentSortedListValueTypeKey<string, VesselProject> BuildPlans = new PersistentSortedListValueTypeKey<string, VesselProject>();

        [KSPField(isPersistant = true)]
        public PersistentList<SpaceCenter> KSCs = new PersistentList<SpaceCenter>();
        public SpaceCenter ActiveSC = null;

        [KSPField(isPersistant = true)]
        public VesselProject LaunchedVessel = new VesselProject();
        [KSPField(isPersistant = true)]
        public VesselProject EditedVessel = new VesselProject();
        [KSPField(isPersistant = true)]
        public VesselProject RecoveredVessel = new VesselProject();

        [KSPField(isPersistant = true)]
        public PersistentList<PartCrewAssignment> LaunchedCrew = new PersistentList<PartCrewAssignment>();

        [KSPField(isPersistant = true)]
        public AirlaunchParams AirlaunchParams = new AirlaunchParams();

        [KSPField(isPersistant = true)]
        public FundTargetProject fundTarget = new FundTargetProject();

        public bool MergingAvailable;
        public List<VesselProject> MergedVessels = new List<VesselProject>();

        public static KerbalConstructionTimeData Instance { get; protected set; }

        public override void OnAwake()
        {
            base.OnAwake();
            if (Instance != null)
                Destroy(Instance);

            Instance = this;
        }

        public void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        public void RecalculateBuildRates()
        {
            LCEfficiency.RecalculateConstants();

            foreach (var ksc in KSCs)
                ksc.RecalculateBuildRates(true);

            for (int i = TechList.Count; i-- > 0;)
            {
                ResearchProject tech = TechList[i];
                tech.UpdateBuildRate(i);
            }

            Crew.CrewHandler.Instance?.RecalculateBuildRates();

            KCTEvents.OnRecalculateBuildRates.Fire();
        }

        #region Persistence

        public override void OnSave(ConfigNode node)
        {
            if (KCTUtilities.CurrentGameIsMission()) return;

            RP0Debug.Log("Writing to persistence.");
            base.OnSave(node);

            KCT_GUI.GuiDataSaver.Save();
        }

        public override void OnLoad(ConfigNode node)
        {
            try
            {
                base.OnLoad(node);
                Database.LoadTree();

                if (KCTUtilities.CurrentGameIsMission()) return;

                RP0Debug.Log("Reading from persistence.");

                TechList.Updated += techListUpdated;

                bool foundStockKSC = false;
                foreach (var ksc in KSCs)
                {
                    if (ksc.KSCName.Length > 0 && string.Equals(ksc.KSCName, _legacyDefaultKscId, StringComparison.OrdinalIgnoreCase))
                    {
                        foundStockKSC = true;
                        break;
                    }
                }

                SetActiveKSCToRSS();
                if (foundStockKSC)
                    TryMigrateStockKSC();

                // Prune bad or inactive KSCs.
                for (int i = KSCs.Count; i-- > 0;)
                {
                    SpaceCenter ksc = KSCs[i];
                    if (ksc.KSCName == null || ksc.KSCName.Length == 0 || (ksc.IsEmpty && ksc != ActiveSC))
                        KSCs.RemoveAt(i);
                }

                foreach (var blv in BuildPlans.Values)
                    blv.LinkToLC(null);

                LaunchedVessel.LinkToLC(LC(LaunchedVessel.LCID));
                RecoveredVessel.LinkToLC(LC(RecoveredVessel.LCID));
                EditedVessel.LinkToLC(LC(EditedVessel.LCID));

                LCEfficiency.RelinkAll();

                if (LoadedSaveVersion < VERSION)
                {
                    if (LoadedSaveVersion < 4)
                    {
                        foreach (var ksc in KSCs)
                        {
                            foreach (var lc in ksc.LaunchComplexes)
                            {
                                foreach (var blv in lc.BuildList)
                                    blv.RecalculateFromNode(true);
                                foreach (var blv in lc.Warehouse)
                                    blv.RecalculateFromNode(true);
                            }
                        }
                    }
                    LoadedSaveVersion = VERSION;
                }
            }
            catch (Exception ex)
            {
                KerbalConstructionTime.ErroredDuringOnLoad = true;
                RP0Debug.LogError("ERROR! An error while KCT loading data occurred. Things will be seriously broken!\n" + ex);
                PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), "errorPopup", "Error Loading RP-1 Data", "ERROR! An error occurred while loading RP-1 data. Things will be seriously broken! Please report this error to RP-1 GitHub and attach the log file. The game will be UNPLAYABLE in this state!", "Understood", false, HighLogic.UISkin).HideGUIsWhilePopup();
            }
        }

        private void TryMigrateStockKSC()
        {
            SpaceCenter stockKsc = KSCs.Find(k => string.Equals(k.KSCName, _legacyDefaultKscId, StringComparison.OrdinalIgnoreCase));
            if (KSCs.Count == 1)
            {
                // Rename the stock KSC to the new default (Cape)
                stockKsc.KSCName = _defaultKscId;
                SetActiveKSC(stockKsc.KSCName);
                return;
            }

            if (stockKsc.IsEmpty)
            {
                // Nothing provisioned into the stock KSC so it's safe to just delete it
                KSCs.Remove(stockKsc);
                SetActiveKSCToRSS();
                return;
            }

            int numOtherUsedKSCs = KSCs.Count(k => !k.IsEmpty && k != stockKsc);
            if (numOtherUsedKSCs == 0)
            {
                string kscName = GetActiveRSSKSC() ?? _defaultKscId;
                SpaceCenter newDefault = KSCs.Find(k => string.Equals(k.KSCName, kscName, StringComparison.OrdinalIgnoreCase));
                if (newDefault != null)
                {
                    // Stock KSC isn't empty but the new default one is - safe to rename the stock and remove the old default item
                    stockKsc.KSCName = newDefault.KSCName;
                    KSCs.Remove(newDefault);
                    SetActiveKSC(stockKsc);
                    return;
                }
            }

            // Can't really do anything if there's multiple KSCs in use.
            if (!IsKSCSwitcherInstalled)
            {
                // Need to switch back to the legacy "Stock" KSC if KSCSwitcher isn't installed
                SetActiveKSC(stockKsc.KSCName);
            }
        }

        #endregion

        #region Tech
        public bool TechListHas(string techID)
        {
            return TechListIndex(techID) != -1;
        }

        public int TechListIndex(string techID)
        {
            for (int i = TechList.Count; i-- > 0;)
                if (TechList[i].techID == techID)
                    return i;

            return -1;
        }

        public void UpdateTechTimes()
        {
            for (int j = 0; j < TechList.Count; j++)
                TechList[j].UpdateBuildRate(j);
        }

        private void techListUpdated()
        {
            if (TechListIgnoreUpdates)
                return;

            TechListUpdated();
        }

        public void TechListUpdated()
        {
            MaintenanceHandler.Instance?.ScheduleMaintenanceUpdate();
            Harmony.PatchRDTechTree.Instance?.RefreshUI();
        }

        #endregion

        #region LC

        public void RegisterLC(LaunchComplex lc)
        {
            _LCIDtoLC[lc.ID] = lc;
        }

        public bool UnregisterLC(LaunchComplex lc)
        {
            return _LCIDtoLC.Remove(lc.ID);
        }

        public void RegisterLP(LCLaunchPad lp)
        {
            _LPIDtoLP[lp.id] = lp;
        }

        public bool UnregsiterLP(LCLaunchPad lp)
        {
            return _LPIDtoLP.Remove(lp.id);
        }

        public LaunchComplex FindLCFromID(Guid guid)
        {
            return LC(guid);
        }

        #endregion

        #region KSCSwitcher section

        private static bool? _isKSCSwitcherInstalled = null;
        private static FieldInfo _fiKSCSwInstance;
        private static FieldInfo _fiKSCSwSites;
        private static FieldInfo _fiKSCSwLastSite;
        private static FieldInfo _fiKSCSwDefaultSite;
        private const string _legacyDefaultKscId = "Stock";
        private const string _defaultKscId = "us_cape_canaveral";

        private static bool IsKSCSwitcherInstalled
        {
            get
            {
                if (!_isKSCSwitcherInstalled.HasValue)
                {
                    Assembly a = AssemblyLoader.loadedAssemblies.FirstOrDefault(la => string.Equals(la.name, "KSCSwitcher", StringComparison.OrdinalIgnoreCase))?.assembly;
                    _isKSCSwitcherInstalled = a != null;
                    if (_isKSCSwitcherInstalled.Value)
                    {
                        Type t = a.GetType("regexKSP.KSCLoader");
                        _fiKSCSwInstance = t?.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                        _fiKSCSwSites = t?.GetField("Sites", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

                        t = a.GetType("regexKSP.KSCSiteManager");
                        _fiKSCSwLastSite = t?.GetField("lastSite", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        _fiKSCSwDefaultSite = t?.GetField("defaultSite", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

                        if (_fiKSCSwInstance == null || _fiKSCSwSites == null || _fiKSCSwLastSite == null || _fiKSCSwDefaultSite == null)
                        {
                            RP0Debug.LogError("Failed to bind to KSCSwitcher");
                            _isKSCSwitcherInstalled = false;
                        }
                    }
                }
                return _isKSCSwitcherInstalled.Value;
            }
        }

        private string GetActiveRSSKSC()
        {
            if (!IsKSCSwitcherInstalled) return null;

            // get the LastKSC.KSCLoader.instance object
            // check the Sites object (KSCSiteManager) for the lastSite, if "" then get defaultSite

            object loaderInstance = _fiKSCSwInstance.GetValue(null);
            if (loaderInstance == null)
                return null;
            object sites = _fiKSCSwSites.GetValue(loaderInstance);
            string lastSite = _fiKSCSwLastSite.GetValue(sites) as string;

            if (lastSite == string.Empty)
                lastSite = _fiKSCSwDefaultSite.GetValue(sites) as string;
            return lastSite;
        }

        #endregion

        #region KSC

        public void SetActiveKSCToRSS()
        {
            string site = GetActiveRSSKSC();
            SetActiveKSC(site);
        }

        public void SetActiveKSC(string site)
        {
            if (site == null || site.Length == 0)
                site = _defaultKscId;
            if (ActiveSC == null || site != ActiveSC.KSCName)
            {
                RP0Debug.Log($"Setting active site to {site}");
                SpaceCenter newKsc = KSCs.FirstOrDefault(ksc => ksc.KSCName == site);
                if (newKsc == null)
                {
                    newKsc = new SpaceCenter(site);
                    newKsc.EnsureStartingLaunchComplexes();
                    KSCs.Add(newKsc);
                }

                SetActiveKSC(newKsc);
            }
        }

        public void SetActiveKSC(SpaceCenter ksc)
        {
            if (ksc == null || ksc == ActiveSC)
                return;

            // TODO: Allow setting KSC outside the tracking station
            // which will require doing some work on KSC switch
            ActiveSC = ksc;
        }

        #endregion

        #region Budget

        public double GetEffectiveIntegrationEngineersForSalary(SpaceCenter ksc)
        {
            double engineers = 0d;
            foreach (var lc in ksc.LaunchComplexes)
                engineers += GetEffectiveEngineersForSalary(lc);
            return engineers + ksc.UnassignedEngineers * Database.SettingsSC.IdleSalaryMult;
        }

        public double GetEffectiveEngineersForSalary(SpaceCenter ksc) => GetEffectiveIntegrationEngineersForSalary(ksc);

        public double GetEffectiveEngineersForSalary(LaunchComplex lc)
        {
            if (lc.IsOperational && lc.Engineers > 0)
            {
                if (!lc.IsActive)
                    return lc.Engineers * Database.SettingsSC.IdleSalaryMult;

                if (lc.IsHumanRated && lc.BuildList.Count > 0 && !lc.BuildList[0].humanRated)
                {
                    int num = Math.Min(lc.Engineers, lc.MaxEngineersFor(lc.BuildList[0]));
                    return num * lc.RushSalary + (lc.Engineers - num) * Database.SettingsSC.IdleSalaryMult;
                }

                return lc.Engineers * lc.RushSalary;
            }

            return 0;
        }

        public double GetBudgetDelta(double deltaTime)
        {
            // note NetUpkeepPerDay is negative or 0.

            double averageSubsidyPerDay = CurrencyUtils.Funds(TransactionReasonsRP0.Subsidy, MaintenanceHandler.GetAverageSubsidyForPeriod(deltaTime)) * (1d / 365.25d);
            double fundDelta = Math.Min(0d, MaintenanceHandler.Instance.UpkeepPerDayForDisplay + averageSubsidyPerDay) * deltaTime * (1d / 86400d)
                + GetConstructionCostOverTime(deltaTime) + GetRolloutCostOverTime(deltaTime) + GetAirlaunchCostOverTime(deltaTime)
                + Programs.ProgramHandler.Instance.GetDisplayProgramFunding(deltaTime);

            return fundDelta;
        }

        public double GetConstructionCostOverTime(double time)
        {
            double delta = 0;
            foreach (var ksc in KSCs)
            {
                delta += GetConstructionCostOverTime(time, ksc);
            }
            return delta;
        }

        public double GetConstructionCostOverTime(double time, SpaceCenter ksc)
        {
            double delta = 0;
            foreach (var c in ksc.Constructions)
                delta += c.GetConstructionCostOverTime(time);

            return delta;
        }

        public double GetConstructionCostOverTime(double time, string kscName)
        {
            foreach (var ksc in KSCs)
            {
                if (ksc.KSCName == kscName)
                {
                    return GetConstructionCostOverTime(time, ksc);
                }
            }

            return 0d;
        }

        public double GetRolloutCostOverTime(double time)
        {
            double delta = 0;
            foreach (var ksc in KSCs)
            {
                delta += GetRolloutCostOverTime(time, ksc);
            }
            return delta;
        }

        public double GetRolloutCostOverTime(double time, SpaceCenter ksc)
        {
            double delta = 0;
            for (int i = 1; i < ksc.LaunchComplexes.Count; ++i)
                delta += GetRolloutCostOverTime(time, ksc.LaunchComplexes[i]);

            return delta;
        }

        public double GetRolloutCostOverTime(double time, LaunchComplex lc)
        {
            double delta = 0;
            foreach (var rr in lc.Recon_Rollout)
            {
                if (rr.RRType != ReconRolloutProject.RolloutReconType.Rollout)
                    continue;

                double t = rr.GetTimeLeft();
                double fac = 1d;
                if (t > time)
                    fac = time / t;

                delta += CurrencyUtils.Funds(TransactionReasonsRP0.RocketRollout, -rr.cost * (1d - rr.progress / rr.BP) * fac);
            }

            return delta;
        }

        public double GetAirlaunchCostOverTime(double time)
        {
            double delta = 0;
            foreach (var ksc in KSCs)
            {
                delta += GetAirlaunchCostOverTime(time, ksc);
            }
            return delta;
        }

        public double GetAirlaunchCostOverTime(double time, SpaceCenter ksc)
        {
            double delta = 0;
            foreach (var al in ksc.Hangar.Airlaunch_Prep)
            {
                if (al.direction == AirlaunchProject.PrepDirection.Mount)
                {
                    double t = al.GetTimeLeft();
                    double fac = 1d;
                    if (t > time)
                        fac = time / t;

                    delta += CurrencyUtils.Funds(TransactionReasonsRP0.AirLaunchRollout, -al.cost * (1d - al.progress / al.BP) * fac);
                }
            }

            return delta;
        }

        public double GetRolloutCostOverTime(double time, string kscName)
        {
            foreach (var ksc in KSCs)
            {
                if (ksc.KSCName == kscName)
                {
                    return GetRolloutCostOverTime(time, ksc);
                }
            }

            return 0d;
        }

        public int TotalEngineers
        {
            get
            {
                int eng = 0;
                foreach (var ksc in KSCs)
                    eng += ksc.Engineers;

                return eng;
            }
        }

        public double WeightedAverageEfficiencyEngineers
        {
            get
            {
                double effic = 0d;
                int engineers = 0;
                foreach (var ksc in KSCs)
                {
                    foreach (var lc in ksc.LaunchComplexes)
                    {
                        if (!lc.IsOperational || lc.LCType == LaunchComplexType.Hangar)
                            continue;

                        if (lc.Engineers == 0d)
                            continue;

                        engineers += lc.Engineers;
                        effic += lc.Efficiency * engineers;
                    }
                }

                if (engineers == 0)
                    return 0d;

                return effic / engineers;
            }
        }
        #endregion
    }
}

/*
    KerbalConstructionTime (c) by Michael Marvin, Zachary Eck

    KerbalConstructionTime is licensed under a
    Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International License.

    You should have received a copy of the license along with this
    work. If not, see <http://creativecommons.org/licenses/by-nc-sa/4.0/>.
*/
