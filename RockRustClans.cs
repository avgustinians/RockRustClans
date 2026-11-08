using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using ConVar;

namespace Oxide.Plugins
{
    [Info("RockRustClans", "Avgust", "3.0.1")]
    [Description("Clan system with GUI, wipe rating, Rust team binding and monument captures")]
    public class RockRustClans : RustPlugin
    {
        #region Constants

        private const string PermissionAdmin = "rockrustclans.admin";
        private const string DataFileName = "RockRustClans";
        private const string UiRoot = "RRClans.UI";
        private const string AdminUiRoot = "RRClans.AdminUI";
        private const string HudRoot = "RRClans.HUD";
        private const string LowGradeShortname = "lowgradefuel";

        private readonly Regex _tagRegex = new Regex("^[A-Z0-9]{2,6}$", RegexOptions.Compiled);
        private readonly Dictionary<string, RuntimeTerritory> _runtimeTerritories = new Dictionary<string, RuntimeTerritory>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<ulong> _captureGeneratorNetIds = new HashSet<ulong>();
        private readonly HashSet<ulong> _captureStationNetIds = new HashSet<ulong>();
        private readonly Dictionary<ulong, string> _openTabs = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> _clanInfoPanels = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> _clanNavSignatures = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> _clanHeaderSignatures = new Dictionary<ulong, string>();
        private readonly HashSet<ulong> _clanUiOpen = new HashSet<ulong>();
        private readonly HashSet<ulong> _adminUiOpen = new HashSet<ulong>();
        private readonly Dictionary<ulong, string> _helpSections = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> _adminSelectedClans = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> _adminUiModes = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, int> _adminScoreAdjustments = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, long> _leaderDisbandConfirmUntil = new Dictionary<ulong, long>();
        private readonly Dictionary<ulong, string> _adminDeleteConfirmTag = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, int> _adminPages = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, PendingChatInput> _pendingChatInputs = new Dictionary<ulong, PendingChatInput>();
        private Timer _captureTimer;
        private Timer _dailyTimer;
        private PluginConfig _config;
        private StoredData _data;
        private bool _serverReady;
        private string _generatorPrefabPath;
        private string _captureStationPrefabPath;
        private bool _manualCaptureFuelConsume;

        [PluginReference]
        private Plugin BetterChat;

        // у BetterChat свой хук чата, поэтому запоминаем сообщение, которое ушло в ввод GUI
        private readonly Dictionary<ulong, string> _betterChatSuppressOnce = new Dictionary<ulong, string>();

        private class PendingChatInput
        {
            public string Action;
            public string ClanId;
            public long ExpiresUnix;
        }

        #endregion

        #region Configuration

        private class PluginConfig
        {
            [JsonProperty("Доступ к системе")]
            public AccessSettings Access = new AccessSettings();

            [JsonProperty("Настройки кланов")]
            public ClanSettings Clan = new ClanSettings();

            [JsonProperty("Настройки очков")]
            public ScoreSettings Score = new ScoreSettings();

            [JsonProperty("Настройки захвата")]
            public CaptureSettings Capture = new CaptureSettings();

            [JsonProperty("Настройки чата")]
            public ChatSettings Chat = new ChatSettings();

            [JsonProperty("Настройки админ-панели")]
            public AdminUiSettings AdminUi = new AdminUiSettings();

            // Без значения по умолчанию: иначе Newtonsoft при каждой загрузке
            // дописывает сохранённые территории к стандартным и список растёт.
            [JsonProperty("Территории")]
            public List<TerritoryConfig> Territories;
        }

        private static List<TerritoryConfig> DefaultTerritories()
        {
            return new List<TerritoryConfig>
        {
            new TerritoryConfig
            {
                Id = "airfield",
                DisplayName = "Аэродром",
                PrefabContains = "airfield_1",
                Enabled = true,
                Radius = 115f,
                LocalOffset = new SerializableVector3(0f, 0f, 0f),
                Calibrated = false,
                IconShortname = "supply.signal"
            },
            new TerritoryConfig
            {
                Id = "launchsite",
                DisplayName = "Космодром",
                PrefabContains = "launch_site_1",
                Enabled = true,
                Radius = 145f,
                LocalOffset = new SerializableVector3(0f, 0f, 0f),
                Calibrated = false,
                IconShortname = "rocket.launcher"
            },
            new TerritoryConfig
            {
                Id = "airport",
                DisplayName = "Аэропорт",
                PrefabContains = "airport",
                Enabled = false,
                Radius = 115f,
                LocalOffset = new SerializableVector3(0f, 0f, 0f),
                Calibrated = false,
                IconShortname = "map"
            }
        };
        }

        private static List<string> DefaultMemberRoles()
        {
            return new List<string> { "УЧАСТНИК", "ВЕРБОВЩИК", "ФАРМЕР", "ЭЛЕКТРИК", "СТРОИТЕЛЬ", "БОЕЦ", "РЕЙДЕР" };
        }

        private class AccessSettings
        {
            [JsonProperty("Включить систему для обычных игроков")]
            public bool EnableForRegularPlayers = false;

            [JsonProperty("Разрешить администраторам тестировать скрытую систему")]
            public bool AllowAdministratorTesting = true;
        }

        private class ClanSettings
        {
            [JsonProperty("Максимальное количество участников клана")]
            public int MaxMembers = 6;

            [JsonProperty("Срок действия приглашения в секундах")]
            public int InviteLifetimeSeconds = 900;

            [JsonProperty("Минимальная длина тега")]
            public int MinTagLength = 2;

            [JsonProperty("Максимальная длина тега")]
            public int MaxTagLength = 6;

            [JsonProperty("Название роли главы")]
            public string LeaderRoleName = "ГЛАВА";

            [JsonProperty("Название роли заместителя главы")]
            public string DeputyRoleName = "ЗАМЕСТИТЕЛЬ";

            [JsonProperty("Требовать созданную зелёную команду Rust перед созданием клана")]
            public bool RequireGreenTeamBeforeClanCreation = true;

            [JsonProperty("Убирать исключённого или вышедшего игрока из зелёной команды клана")]
            public bool RemoveFromGreenTeamOnLeave = true;

            [JsonProperty("Роль участника по умолчанию")]
            public string DefaultMemberRole = "УЧАСТНИК";

            [JsonProperty("Доступные роли участников")]
            public List<string> MemberRoles;

        }

        private class ScoreSettings
        {
            [JsonProperty("Очки за убийство участника другого клана")]
            public int KillPoints = 2;

            [JsonProperty("Защита от повторного фарма одного игрока в секундах")]
            public int SameVictimCooldownSeconds = 1800;

            [JsonProperty("Очки за успешный захват территории")]
            public int CapturePoints = 75;

            [JsonProperty("Дополнительные очки за захват территории у другого клана")]
            public int TakeoverBonusPoints = 25;

            [JsonProperty("Очки за одно начисление удержания территории")]
            public int HoldPoints = 2;

            [JsonProperty("Интервал начисления очков за удержание в секундах")]
            public int HoldRewardIntervalSeconds = 300;

            [JsonProperty("Минимум участников владельца в зоне для получения очков удержания")]
            public int MinimumMembersInsideForHoldReward = 1;

            [JsonProperty("Сбрасывать рейтинг при новом вайпе карты")]
            public bool ResetOnNewSave = true;

            [JsonProperty("Не начислять удержание, пока территорию захватывает другой клан")]
            public bool PauseHoldWhileEnemyCapturing = true;
        }

        private class CaptureSettings
        {
            [JsonProperty("Длительность захвата в секундах")]
            public int CaptureDurationSeconds = 720;

            [JsonProperty("Интервал проверки захвата в секундах")]
            public int TickSeconds = 10;

            [JsonProperty("Расход ТНК за одну активную проверку")]
            public int FuelPerTick = 1;

            [JsonProperty("Перезарядка территории после захвата в секундах")]
            public int TerritoryCooldownSeconds = 5400;

            [JsonProperty("Перезарядка территории после сорванного захвата в секундах")]
            public int FailedCaptureCooldownSeconds = 300;

            [JsonProperty("Минимум участников клана в зоне для начала захвата")]
            public int MinimumMembersToStart = 2;

            [JsonProperty("Минимум участников клана в зоне для продолжения захвата")]
            public int MinimumMembersToProgress = 1;

            [JsonProperty("Максимум одновременных захватов одним кланом")]
            public int MaxSimultaneousCapturesPerClan = 1;

            [JsonProperty("Потеря прогресса за проверку без защитников")]
            public float ProgressLossWithoutDefendersSeconds = 10f;

            [JsonProperty("Здоровье генератора")]
            public float GeneratorHealth = 1500f;

            [JsonProperty("Максимальная дистанция от точки для начала захвата")]
            public float StartDistance = 80f;

            [JsonProperty("Отменять захват при уничтожении генератора")]
            public bool CancelOnGeneratorDestroyed = true;

            [JsonProperty("Разрешить врагам забирать топливо из генератора")]
            public bool AllowEnemyFuelLooting = true;

            [JsonProperty("Разрешить перехват активного захвата другим кланом")]
            public bool AllowCaptureTakeover = true;

            [JsonProperty("Защита от повторного перехвата после смены клана в секундах")]
            public int TakeoverProtectionSeconds = 45;

            [JsonProperty("Для перехвата генератор должен быть полностью без ТНК")]
            public bool TakeoverRequiresEmptyFuel = true;

            [JsonProperty("Показывать постоянную площадку точки захвата")]
            public bool ShowCaptureStation = true;

            [JsonProperty("Предмет ограждения площадки (shortname)")]
            public string CaptureStationDeployableShortname = "barricade.sandbags";

            [JsonProperty("Радиус ограждения площадки в метрах")]
            public float CaptureStationRadius = 3.2f;
        }

        private class ChatSettings
        {
            [JsonProperty("Показывать тег клана в глобальном чате")]
            public bool EnableGlobalTag = true;

            [JsonProperty("Цвет тега клана")]
            public string TagColor = "#d6a84b";

            [JsonProperty("Цвет имени игрока")]
            public string NameColor = "#e5e5e5";

            [JsonProperty("Цвет кланового чата")]
            public string ClanChatColor = "#74c365";

            [JsonProperty("Выводить клановый чат в консоль сервера")]
            public bool LogClanChatToServerConsole = true;
        }

        private class AdminUiSettings
        {
            [JsonProperty("Малый шаг изменения очков")]
            public int SmallScoreStep = 10;

            [JsonProperty("Средний шаг изменения очков")]
            public int MediumScoreStep = 50;

            [JsonProperty("Большой шаг изменения очков")]
            public int LargeScoreStep = 100;

            [JsonProperty("Максимальная сумма одного изменения очков")]
            public int MaximumScoreAdjustment = 1000;

            [JsonProperty("Количество часов на графиках статистики")]
            public int StatisticsGraphHours = 24;

            [JsonProperty("Максимум записей в журнале статистики")]
            public int StatisticsEventLogLimit = 200;
        }

        private class TerritoryConfig
        {
            [JsonProperty("Идентификатор")]
            public string Id;

            [JsonProperty("Отображаемое название")]
            public string DisplayName;

            [JsonProperty("Часть названия prefab монумента")]
            public string PrefabContains;

            [JsonProperty("Включено")]
            public bool Enabled;

            [JsonProperty("Радиус территории")]
            public float Radius;

            [JsonProperty("Локальное смещение точки генератора")]
            public SerializableVector3 LocalOffset;

            [JsonProperty("Точка генератора настроена администратором")]
            public bool Calibrated;

            [JsonProperty("Иконка в меню (shortname предмета)")]
            public string IconShortname;
        }

        private class SerializableVector3
        {
            [JsonProperty("Координата X")]
            public float X;

            [JsonProperty("Координата Y")]
            public float Y;

            [JsonProperty("Координата Z")]
            public float Z;

            public SerializableVector3() { }

            public SerializableVector3(float x, float y, float z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public Vector3 ToVector3()
            {
                return new Vector3(X, Y, Z);
            }

            public static SerializableVector3 FromVector3(Vector3 value)
            {
                return new SerializableVector3(value.x, value.y, value.z);
            }
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            _config.Territories = DefaultTerritories();
            _config.Clan.MemberRoles = DefaultMemberRoles();
            Config.WriteObject(_config, true);
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                    throw new Exception("Config is null");
            }
            catch (Exception ex)
            {
                PrintError("Ошибка чтения конфигурации: " + ex.Message + ". Создаю конфигурацию по умолчанию.");
                LoadDefaultConfig();
            }

            NormalizeConfig();
            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void NormalizeConfig()
        {
            if (_config.Access == null)
                _config.Access = new AccessSettings();
            if (_config.Clan == null)
                _config.Clan = new ClanSettings();
            if (_config.Score == null)
                _config.Score = new ScoreSettings();
            if (_config.Capture == null)
                _config.Capture = new CaptureSettings();
            if (_config.Chat == null)
                _config.Chat = new ChatSettings();
            if (_config.AdminUi == null)
                _config.AdminUi = new AdminUiSettings();

            _config.Clan.MaxMembers = Mathf.Clamp(_config.Clan.MaxMembers, 2, 8);
            _config.Clan.MinTagLength = Mathf.Clamp(_config.Clan.MinTagLength, 1, 6);
            _config.Clan.MaxTagLength = Mathf.Clamp(_config.Clan.MaxTagLength, _config.Clan.MinTagLength, 8);
            if (string.IsNullOrWhiteSpace(_config.Clan.LeaderRoleName))
                _config.Clan.LeaderRoleName = "ГЛАВА";
            if (string.IsNullOrWhiteSpace(_config.Clan.DeputyRoleName))
                _config.Clan.DeputyRoleName = "ЗАМЕСТИТЕЛЬ";
            if (string.IsNullOrWhiteSpace(_config.Clan.DefaultMemberRole))
                _config.Clan.DefaultMemberRole = "УЧАСТНИК";
            if (_config.Clan.MemberRoles == null || _config.Clan.MemberRoles.Count == 0)
                _config.Clan.MemberRoles = DefaultMemberRoles();
            _config.Clan.MemberRoles = _config.Clan.MemberRoles
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Select(role => role.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();
            if (!_config.Clan.MemberRoles.Any(role => role.Equals(_config.Clan.DefaultMemberRole, StringComparison.OrdinalIgnoreCase)))
                _config.Clan.MemberRoles.Insert(0, _config.Clan.DefaultMemberRole.ToUpperInvariant());
            _config.Clan.DefaultMemberRole = _config.Clan.DefaultMemberRole.Trim().ToUpperInvariant();
            _config.Clan.LeaderRoleName = _config.Clan.LeaderRoleName.Trim().ToUpperInvariant();
            _config.Clan.DeputyRoleName = _config.Clan.DeputyRoleName.Trim().ToUpperInvariant();
            _config.Capture.TickSeconds = Mathf.Clamp(_config.Capture.TickSeconds, 2, 60);
            _config.Capture.CaptureDurationSeconds = Mathf.Max(_config.Capture.TickSeconds, _config.Capture.CaptureDurationSeconds);
            _config.Capture.FuelPerTick = Mathf.Max(1, _config.Capture.FuelPerTick);
            _config.Capture.MinimumMembersToStart = Mathf.Clamp(_config.Capture.MinimumMembersToStart, 1, _config.Clan.MaxMembers);
            _config.Capture.MinimumMembersToProgress = Mathf.Clamp(_config.Capture.MinimumMembersToProgress, 1, _config.Clan.MaxMembers);
            _config.Capture.MaxSimultaneousCapturesPerClan = Mathf.Clamp(_config.Capture.MaxSimultaneousCapturesPerClan, 1, Math.Max(1, _config.Territories == null ? 1 : _config.Territories.Count));
            _config.Capture.CaptureStationRadius = Mathf.Clamp(_config.Capture.CaptureStationRadius, 2.2f, 6f);
            _config.Capture.TakeoverProtectionSeconds = Mathf.Clamp(_config.Capture.TakeoverProtectionSeconds, 0, 300);
            _config.Capture.FailedCaptureCooldownSeconds = Mathf.Max(0, _config.Capture.FailedCaptureCooldownSeconds);
            _config.Capture.TerritoryCooldownSeconds = Mathf.Max(0, _config.Capture.TerritoryCooldownSeconds);
            _config.Capture.GeneratorHealth = Mathf.Max(100f, _config.Capture.GeneratorHealth);
            if (string.IsNullOrWhiteSpace(_config.Capture.CaptureStationDeployableShortname))
                _config.Capture.CaptureStationDeployableShortname = "barricade.sandbags";
            _config.Score.KillPoints = Mathf.Max(0, _config.Score.KillPoints);
            _config.Score.SameVictimCooldownSeconds = Mathf.Max(0, _config.Score.SameVictimCooldownSeconds);
            _config.Score.CapturePoints = Mathf.Max(0, _config.Score.CapturePoints);
            _config.Score.TakeoverBonusPoints = Mathf.Max(0, _config.Score.TakeoverBonusPoints);
            _config.Score.HoldPoints = Mathf.Max(0, _config.Score.HoldPoints);
            _config.Score.HoldRewardIntervalSeconds = Mathf.Max(30, _config.Score.HoldRewardIntervalSeconds);
            _config.Score.MinimumMembersInsideForHoldReward = Mathf.Clamp(_config.Score.MinimumMembersInsideForHoldReward, 0, _config.Clan.MaxMembers);
            _config.AdminUi.SmallScoreStep = Mathf.Clamp(_config.AdminUi.SmallScoreStep, 1, 1000);
            _config.AdminUi.MediumScoreStep = Mathf.Clamp(_config.AdminUi.MediumScoreStep, _config.AdminUi.SmallScoreStep, 5000);
            _config.AdminUi.LargeScoreStep = Mathf.Clamp(_config.AdminUi.LargeScoreStep, _config.AdminUi.MediumScoreStep, 10000);
            _config.AdminUi.MaximumScoreAdjustment = Mathf.Clamp(_config.AdminUi.MaximumScoreAdjustment, _config.AdminUi.LargeScoreStep, 100000);
            _config.AdminUi.StatisticsGraphHours = Mathf.Clamp(_config.AdminUi.StatisticsGraphHours, 6, 48);
            _config.AdminUi.StatisticsEventLogLimit = Mathf.Clamp(_config.AdminUi.StatisticsEventLogLimit, 50, 1000);

            if (_config.Territories == null || _config.Territories.Count == 0)
                _config.Territories = DefaultTerritories();

            // Чиним конфиги, которые раздулись в старых версиях: оставляем одну запись
            // на каждый ID (последнюю — её редактировал администратор), калибровку не теряем.
            int before = _config.Territories.Count;
            List<TerritoryConfig> unique = new List<TerritoryConfig>();
            foreach (IGrouping<string, TerritoryConfig> group in _config.Territories
                .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Id))
                .GroupBy(t => t.Id.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                TerritoryConfig keep = group.Last();
                keep.Id = group.Key;
                TerritoryConfig calibrated = group.LastOrDefault(t => t.Calibrated && t.LocalOffset != null);
                if (!keep.Calibrated && calibrated != null)
                {
                    keep.Calibrated = true;
                    keep.LocalOffset = calibrated.LocalOffset;
                }
                unique.Add(keep);
            }
            _config.Territories = unique;
            if (before != unique.Count)
                PrintWarning(string.Format("Конфиг территорий очищен от дублей: было {0} записей, осталось {1}.", before, unique.Count));

            foreach (TerritoryConfig territory in _config.Territories)
            {
                if (territory.LocalOffset == null)
                    territory.LocalOffset = new SerializableVector3(0f, 0f, 0f);
                territory.Radius = Mathf.Max(20f, territory.Radius);
                if (string.IsNullOrWhiteSpace(territory.IconShortname))
                    territory.IconShortname = DefaultTerritoryIcon(territory.Id);
            }
        }

        #endregion

        #region Data

        private class StoredData
        {
            public Dictionary<string, ClanData> Clans = new Dictionary<string, ClanData>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<ulong, string> PlayerClan = new Dictionary<ulong, string>();
            public Dictionary<ulong, ClanInvite> Invites = new Dictionary<ulong, ClanInvite>();
            public Dictionary<string, TerritoryState> Territories = new Dictionary<string, TerritoryState>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, TerritoryPointData> CapturePoints = new Dictionary<string, TerritoryPointData>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, long> KillRewardCooldowns = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            public PluginStatistics Statistics = new PluginStatistics();
            public List<PluginEventEntry> StatisticsEvents = new List<PluginEventEntry>();
            public List<HourlyStatisticsBucket> HourlyStatistics = new List<HourlyStatisticsBucket>();
            public string DailyKey = string.Empty;
            public string LastWinnerTag = string.Empty;
            public int LastWinnerScore;
            public long SeasonStartedUnix;
        }

        private class ClanData
        {
            public string ClanId;
            public string Tag;
            public string DisplayName;
            public ulong OwnerId;
            public ulong DeputyId;
            public List<ulong> Members = new List<ulong>();
            public Dictionary<ulong, string> Names = new Dictionary<ulong, string>();
            public Dictionary<ulong, string> MemberRoles = new Dictionary<ulong, string>();
            public Dictionary<ulong, int> PlayerKills = new Dictionary<ulong, int>();
            public ulong TeamId;
            public bool ManualTeamCreated;
            public int Score;
            public int KillScore;
            public int CaptureScore;
            public int HoldScore;
            public int AdminScore;
            public int DailyScore;
            public int DailyKillScore;
            public int Kills;
            public int Captures;
            public int DailyCaptures;
            public long LastScoreUnix;
            public long LastTagChangeUnix;
            public bool NameChangedThisWipe;
            public bool TagChangedThisWipe;
            public long CreatedUnix;
        }

        private class ClanInvite
        {
            public string ClanTag;
            public ulong InviterId;
            public long ExpiresUnix;
        }

        private class TerritoryPointData
        {
            public bool Calibrated;
            public SerializableVector3 LocalOffset = new SerializableVector3(0f, 0f, 0f);
        }

        private class TerritoryState
        {
            public string Id;
            public string OwnerTag = string.Empty;
            public string CapturingTag = string.Empty;
            public float ProgressSeconds;
            public long CaptureStartedUnix;
            public long CooldownUntilUnix;
            public long LastCapturedUnix;
            public int HoldTicksAwarded;
            public long NextHoldRewardUnix;
            public ulong GeneratorNetId;
            public long TakeoverLockedUntilUnix;
            public string Status = "idle";
        }

        private class PluginStatistics
        {
            public int CaptureAttempts;
            public int CaptureSuccesses;
            public int CaptureFailures;
            public int CaptureManualCancels;
            public int CaptureTakeovers;
            public int GeneratorDestroyedFailures;
            public int PvpKills;
            public int PvpPoints;
            public int CapturePoints;
            public int HoldPoints;
            public long LastActivityUnix;
        }

        private class PluginEventEntry
        {
            public long Unix;
            public string Type = string.Empty;
            public string ClanTag = string.Empty;
            public string TerritoryId = string.Empty;
            public string Text = string.Empty;
        }

        private class HourlyStatisticsBucket
        {
            public long HourUnix;
            public int CaptureStarts;
            public int CaptureSuccesses;
            public int CaptureFailures;
            public int CaptureManualCancels;
            public int CaptureTakeovers;
            public int PvpKills;
        }

        private void LoadData()
        {
            try
            {
                _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFileName);
            }
            catch (Exception ex)
            {
                PrintWarning("Не удалось прочитать data-файл: " + ex.Message + ". Создаю новый.");
                _data = new StoredData();
            }

            if (_data == null)
                _data = new StoredData();
            if (_data.Clans == null)
                _data.Clans = new Dictionary<string, ClanData>(StringComparer.OrdinalIgnoreCase);
            if (_data.PlayerClan == null)
                _data.PlayerClan = new Dictionary<ulong, string>();
            if (_data.Invites == null)
                _data.Invites = new Dictionary<ulong, ClanInvite>();
            if (_data.Territories == null)
                _data.Territories = new Dictionary<string, TerritoryState>(StringComparer.OrdinalIgnoreCase);
            if (_data.CapturePoints == null)
                _data.CapturePoints = new Dictionary<string, TerritoryPointData>(StringComparer.OrdinalIgnoreCase);
            if (_data.KillRewardCooldowns == null)
                _data.KillRewardCooldowns = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (_data.Statistics == null)
                _data.Statistics = new PluginStatistics();
            if (_data.StatisticsEvents == null)
                _data.StatisticsEvents = new List<PluginEventEntry>();
            if (_data.HourlyStatistics == null)
                _data.HourlyStatistics = new List<HourlyStatisticsBucket>();

            foreach (ClanData clan in _data.Clans.Values)
            {
                if (clan.Members == null) clan.Members = new List<ulong>();
                if (clan.Names == null) clan.Names = new Dictionary<ulong, string>();
                if (clan.MemberRoles == null) clan.MemberRoles = new Dictionary<ulong, string>();
                if (clan.PlayerKills == null) clan.PlayerKills = new Dictionary<ulong, int>();
                if (string.IsNullOrEmpty(clan.ClanId)) clan.ClanId = Guid.NewGuid().ToString("N");
                if (string.IsNullOrEmpty(clan.DisplayName)) clan.DisplayName = clan.Tag;

                foreach (ulong memberId in clan.MemberRoles.Keys.Where(id => id == clan.OwnerId || !clan.Members.Contains(id)).ToList())
                    clan.MemberRoles.Remove(memberId);

                foreach (ulong memberId in clan.Members.Where(id => id != clan.OwnerId))
                {
                    string role;
                    if (!clan.MemberRoles.TryGetValue(memberId, out role) || !IsConfiguredMemberRole(role))
                        clan.MemberRoles[memberId] = _config.Clan.DefaultMemberRole;
                    if (!clan.PlayerKills.ContainsKey(memberId))
                        clan.PlayerKills[memberId] = 0;
                }
                if (!clan.PlayerKills.ContainsKey(clan.OwnerId))
                    clan.PlayerKills[clan.OwnerId] = 0;
                foreach (ulong staleId in clan.PlayerKills.Keys.Where(id => !clan.Members.Contains(id)).ToList())
                    clan.PlayerKills.Remove(staleId);
                if (clan.DeputyId == clan.OwnerId || (clan.DeputyId != 0UL && !clan.Members.Contains(clan.DeputyId)))
                    clan.DeputyId = 0UL;

                if (!clan.ManualTeamCreated)
                {
                    // Старые версии автоматически создавали team. После обновления плагин
                    // перестаёт считать такую команду своей и больше ей не управляет.
                    clan.TeamId = 0UL;
                }
                else if (clan.TeamId == 0UL)
                {
                    clan.ManualTeamCreated = false;
                }
                else if (RelationshipManager.ServerInstance != null && RelationshipManager.ServerInstance.FindTeam(clan.TeamId) == null)
                {
                    clan.TeamId = 0UL;
                    clan.ManualTeamCreated = false;
                    clan.DeputyId = 0UL;
                }
                else if (clan.DeputyId != 0UL && RelationshipManager.ServerInstance != null)
                {
                    RelationshipManager.PlayerTeam boundTeam = RelationshipManager.ServerInstance.FindTeam(clan.TeamId);
                    if (boundTeam == null || !boundTeam.members.Contains(clan.DeputyId))
                        clan.DeputyId = 0UL;
                }
            }

            foreach (TerritoryConfig config in _config.Territories)
            {
                TerritoryPointData pointData;
                if (_data.CapturePoints.TryGetValue(config.Id, out pointData) && pointData != null && pointData.Calibrated && pointData.LocalOffset != null)
                {
                    config.LocalOffset = pointData.LocalOffset;
                    config.Calibrated = true;
                }
                else if (config.Calibrated && config.LocalOffset != null)
                {
                    _data.CapturePoints[config.Id] = new TerritoryPointData
                    {
                        Calibrated = true,
                        LocalOffset = SerializableVector3.FromVector3(config.LocalOffset.ToVector3())
                    };
                }

                TerritoryState state;
                if (!_data.Territories.TryGetValue(config.Id, out state))
                {
                    state = new TerritoryState { Id = config.Id };
                    _data.Territories[config.Id] = state;
                }

                state.CapturingTag = string.Empty;
                state.ProgressSeconds = 0f;
                state.GeneratorNetId = 0UL;
                state.Status = "idle";
            }

            RebuildPlayerClanIndex();
            FlushData();
            SaveConfig();
        }

        // Запись на диск — тяжёлая операция. Помечаем данные изменёнными,
        // а пишем файл не чаще раза в 10 секунд (и обязательно при сохранении сервера/выгрузке).
        private bool _dataDirty;
        private Timer _saveTimer;

        private void SaveData()
        {
            _dataDirty = true;
        }

        private void FlushData()
        {
            if (_data == null)
                return;
            _dataDirty = false;
            Interface.Oxide.DataFileSystem.WriteObject(DataFileName, _data);
        }

        private void RebuildPlayerClanIndex()
        {
            _data.PlayerClan.Clear();
            foreach (ClanData clan in _data.Clans.Values)
            {
                clan.Members = clan.Members.Distinct().ToList();
                if (!clan.Members.Contains(clan.OwnerId))
                    clan.Members.Insert(0, clan.OwnerId);

                foreach (ulong id in clan.Members)
                    _data.PlayerClan[id] = clan.Tag;
            }
        }

        #endregion

        #region Oxide Hooks

        private void Init()
        {
            permission.RegisterPermission(PermissionAdmin, this);
            LoadData();
        }

        private void OnServerInitialized(bool initial)
        {
            _serverReady = true;
            ResolveGeneratorPrefabPath();
            ResolveCaptureStationPrefabPath();
            BuildRuntimeTerritories();
            StartTimers();

            foreach (BasePlayer player in BasePlayer.activePlayerList)
                UpdateKnownName(player);

            PrintWarning(string.Format("RockRustClans v{1} загружен. Доступ обычным игрокам: {0}. Для включения измените Доступ к системе -> Включить систему для обычных игроков на true и перезагрузите плагин.", IsPublicSystemEnabled ? "ВКЛЮЧЁН" : "ВЫКЛЮЧЕН", Version));
            PrintWarning("Автоматической синхронизации с зелёной командой нет. Глава управляет ею вручную во вкладке ЗЕЛЁНКА.");
            PrintWarning("Всё управление — в админ-панели: /clanadmin. Точку РТ удобно ставить во вкладке ТЕРРИТОРИИ кнопкой ТОЧКА ЗДЕСЬ.");
        }

        private void OnServerSave()
        {
            FlushData();
        }

        private void Unload()
        {
            _serverReady = false;
            if (_captureTimer != null) _captureTimer.Destroy();
            if (_dailyTimer != null) _dailyTimer.Destroy();
            if (_saveTimer != null) _saveTimer.Destroy();

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, UiRoot);
                CuiHelper.DestroyUi(player, AdminUiRoot);
                CuiHelper.DestroyUi(player, HudRoot);
            }
            _pendingChatInputs.Clear();
            _clanUiOpen.Clear();
            _adminUiOpen.Clear();
            _helpSections.Clear();
            _adminSelectedClans.Clear();
            _adminUiModes.Clear();
            _adminScoreAdjustments.Clear();

            foreach (RuntimeTerritory territory in _runtimeTerritories.Values)
            {
                KillCaptureGenerator(territory, false);
                KillCaptureStation(territory);
            }

            FlushData();
        }

        private void OnNewSave(string filename)
        {
            NextTick(() =>
            {
                if (_config.Score.ResetOnNewSave)
                {
                    ResetSeason(true, true);
                    return;
                }

                ResetClanEditLimitsForNewWipe();
                SaveData();
            });
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            UpdateKnownName(player);
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (player == null) return;
            UpdateKnownName(player);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            _pendingChatInputs.Remove(player.userID);
            _openTabs.Remove(player.userID);
            _leaderDisbandConfirmUntil.Remove(player.userID);
            _adminDeleteConfirmTag.Remove(player.userID);
            _adminPages.Remove(player.userID);
            _clanUiOpen.Remove(player.userID);
            _adminUiOpen.Remove(player.userID);
            _helpSections.Remove(player.userID);
            _clanInfoPanels.Remove(player.userID);
            _clanNavSignatures.Remove(player.userID);
            _clanHeaderSignatures.Remove(player.userID);
            _adminSelectedClans.Remove(player.userID);
            _adminUiModes.Remove(player.userID);
            _adminScoreAdjustments.Remove(player.userID);
            _confirmations.Remove(player.userID);
            _adminSearch.Remove(player.userID);
            _hudVisible.Remove(player.userID);
            _liveMajorSig.Remove(player.userID);
            _liveRefreshAt.Remove(player.userID);
            _adminLogPages.Remove(player.userID);
            CuiHelper.DestroyUi(player, HudRoot);
        }

        private object OnPlayerDeath(BasePlayer victim, HitInfo info)
        {
            if (!IsPublicSystemEnabled)
                return null;

            if (victim == null || info == null)
                return null;

            BasePlayer attacker = info.InitiatorPlayer;
            if (attacker == null || attacker == victim || attacker.userID == 0UL || victim.userID == 0UL)
                return null;
            if (!victim.IsConnected)
                return null;

            ClanData attackerClan = GetClan(attacker.userID);
            ClanData victimClan = GetClan(victim.userID);
            if (attackerClan == null || victimClan == null || string.Equals(attackerClan.Tag, victimClan.Tag, StringComparison.OrdinalIgnoreCase))
                return null;

            string key = attacker.userID + ":" + victim.userID;
            long now = UnixNow();
            long last;
            if (_data.KillRewardCooldowns.TryGetValue(key, out last) && now - last < _config.Score.SameVictimCooldownSeconds)
                return null;

            _data.KillRewardCooldowns[key] = now;
            // Убийство засчитывается всегда, даже если в конфиге за него 0 очков.
            int awarded = AddScore(attackerClan, _config.Score.KillPoints, ScoreSource.Kill);
            ulong attackerId = attacker.userID;
            attackerClan.Kills++;
            if (attackerClan.PlayerKills == null)
                attackerClan.PlayerKills = new Dictionary<ulong, int>();
            int personalKills;
            attackerClan.PlayerKills.TryGetValue(attackerId, out personalKills);
            attackerClan.PlayerKills[attackerId] = personalKills + 1;
            RecordPvpStatistic(attackerClan, attacker, victim, awarded);
            if (awarded > 0)
                SendReply(attacker, string.Format("<color=#9ad06d>+{0}</color> очк. клану <color={1}>[{2}]</color> за убийство участника вражеского клана.", awarded, _config.Chat.TagColor, attackerClan.Tag));
            SaveData();

            return null;
        }

        private object OnPlayerChat(BasePlayer player, string message, Chat.ChatChannel channel)
        {
            if (player == null)
                return null;

            if (!CanAccessSystem(player))
            {
                _pendingChatInputs.Remove(player.userID);
                _betterChatSuppressOnce.Remove(player.userID);
                return null;
            }

            PendingChatInput pending;
            if (_pendingChatInputs.TryGetValue(player.userID, out pending))
            {
                if (pending == null || pending.ExpiresUnix < UnixNow())
                {
                    _pendingChatInputs.Remove(player.userID);
                    SendReply(player, "Время ввода истекло. Откройте <color=#d3654c>/clan</color> и повторите действие.");
                    return false;
                }

                string value = (message ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(value))
                    return false;

                // помечаем, чтобы BetterChat его тоже отменил
                if (BetterChat != null)
                    _betterChatSuppressOnce[player.userID] = message ?? string.Empty;
                _pendingChatInputs.Remove(player.userID);
                HandlePendingChatInput(player, pending, value);
                return false;
            }

            if (!_config.Chat.EnableGlobalTag || channel != Chat.ChatChannel.Global)
                return null;

            ClanData clan = GetClan(player.userID);
            if (clan == null)
                return null;

            // чат форматирует BetterChat, тег клана добавляется в OnBetterChat
            if (BetterChat != null)
                return null;

            string safeName = EscapeRichText(player.displayName);
            string safeMessage = EscapeRichText(message);
            PrintToChat(string.Format("<color={0}>[{1}]</color> <color={2}>{3}</color>: {4}", _config.Chat.TagColor, clan.Tag, _config.Chat.NameColor, safeName, safeMessage));
            return false;
        }

        private object OnBetterChat(Dictionary<string, object> data)
        {
            if (data == null)
                return null;

            object playerObject;
            if (!data.TryGetValue("Player", out playerObject))
                return null;

            Oxide.Core.Libraries.Covalence.IPlayer iPlayer = playerObject as Oxide.Core.Libraries.Covalence.IPlayer;
            if (iPlayer == null)
                return null;

            ulong userId;
            if (!ulong.TryParse(iPlayer.Id, out userId))
                return null;

            if (!CanAccessSystem(userId))
            {
                _betterChatSuppressOnce.Remove(userId);
                return null;
            }

            object messageObject;
            string message = data.TryGetValue("Message", out messageObject) ? Convert.ToString(messageObject) : string.Empty;
            string suppressed;
            if (_betterChatSuppressOnce.TryGetValue(userId, out suppressed) && string.Equals(suppressed ?? string.Empty, message ?? string.Empty, StringComparison.Ordinal))
            {
                _betterChatSuppressOnce.Remove(userId);
                data["CancelOption"] = 2; // отменить и BetterChat, и ванильный вывод
                return data;
            }

            if (!_config.Chat.EnableGlobalTag)
                return null;

            object channelObject;
            if (data.TryGetValue("ChatChannel", out channelObject) && channelObject is Chat.ChatChannel)
            {
                Chat.ChatChannel chatChannel = (Chat.ChatChannel)channelObject;
                if (chatChannel != Chat.ChatChannel.Global)
                    return null;
            }

            ClanData clan = GetClan(userId);
            if (clan == null)
                return null;

            object usernameObject;
            string username = data.TryGetValue("Username", out usernameObject) ? Convert.ToString(usernameObject) : iPlayer.Name;
            string plainNeedle = "[" + clan.Tag + "]";
            if (!string.IsNullOrEmpty(username) && username.IndexOf(plainNeedle, StringComparison.OrdinalIgnoreCase) < 0)
                data["Username"] = string.Format("<color={0}>[{1}]</color> {2}", _config.Chat.TagColor, clan.Tag, username);

            return data;
        }

        private object CanPickupEntity(BasePlayer player, FuelGenerator generator)
        {
            if (generator == null)
                return null;

            RuntimeTerritory territory = FindRuntimeByGenerator(generator.net.ID.Value);
            if (territory != null)
                return false;

            return null;
        }

        private object CanNetworkTo(BaseNetworkable entity, BasePlayer target)
        {
            if (IsPublicSystemEnabled || entity == null || target == null)
                return null;

            BaseEntity baseEntity = entity as BaseEntity;
            if (baseEntity == null || baseEntity.net == null)
                return null;

            ulong netId = baseEntity.net.ID.Value;
            if (!_captureGeneratorNetIds.Contains(netId) && !_captureStationNetIds.Contains(netId))
                return null;

            return CanAccessSystem(target) ? null : (object)false;
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || entity.net == null)
                return null;

            if (_captureStationNetIds.Contains(entity.net.ID.Value))
                return true;

            return null;
        }

        private object OnSwitchToggle(FuelGenerator generator, BasePlayer player)
        {
            if (generator == null) return null;
            RuntimeTerritory territory = FindRuntimeByGenerator(generator.net.ID.Value);
            if (territory == null) return null;
            if (player != null && !CanAccessSystem(player)) return false;
            if (player != null) SendReply(player, "Ивентовый генератор управляется захватом автоматически. Положите в него ТНК (топливо низкого качества).");
            return false;
        }

        // Официальные Oxide item hooks. Они применяются только к генератору,
        // созданному RockRustClans для активного захвата. Обычные генераторы не затрагиваются.
        private void OnItemAddedToContainer(ItemContainer container, Item item)
        {
            FuelGenerator generator = GetEventGeneratorFromContainer(container);
            if (generator == null)
                return;

            RuntimeTerritory territory = FindRuntimeByGenerator(generator.net.ID.Value);
            if (territory != null)
                RefreshEventGeneratorPowerState(territory);
        }

        private void OnItemRemovedFromContainer(ItemContainer container, Item item)
        {
            FuelGenerator generator = GetEventGeneratorFromContainer(container);
            if (generator == null)
                return;

            RuntimeTerritory territory = FindRuntimeByGenerator(generator.net.ID.Value);
            if (territory != null)
                RefreshEventGeneratorPowerState(territory);
        }

        private int? OnItemUse(Item item, int amountToConsume)
        {
            if (_manualCaptureFuelConsume || item == null || item.parent == null)
                return null;

            FuelGenerator generator = GetEventGeneratorFromContainer(item.parent);
            if (generator == null)
                return null;

            // Нативному генератору разрешаем работать и воспроизводить штатный звук,
            // но не даём ему дополнительно сжигать ТНК. Расходом захвата управляет CaptureTick.
            return 0;
        }

        private object CanAcceptItem(ItemContainer container, Item item, int targetPos)
        {
            if (container == null || item == null || item.info == null || container.entityOwner == null)
                return null;

            FuelGenerator generator = container.entityOwner as FuelGenerator;
            if (generator == null)
            {
                ItemBasedFlowRestrictor legacyBox = container.entityOwner as ItemBasedFlowRestrictor;
                BaseEntity parent = legacyBox == null ? null : legacyBox.GetParentEntity();
                generator = parent as FuelGenerator;
            }
            if (generator == null || FindRuntimeByGenerator(generator.net.ID.Value) == null)
                return null;

            if (!string.Equals(item.info.shortname, LowGradeShortname, StringComparison.OrdinalIgnoreCase))
                return ItemContainer.CanAcceptResult.CannotAccept;

            return null;
        }

        private object CanLootEntity(BasePlayer player, FuelGenerator generator)
        {
            if (player == null || generator == null)
                return null;

            RuntimeTerritory territory = FindRuntimeByGenerator(generator.net.ID.Value);
            if (territory == null)
                return null;
            if (!CanAccessSystem(player))
                return false;
            if (_config.Capture.AllowEnemyFuelLooting)
                return null;

            ClanData clan = GetClan(player.userID);
            if (clan == null || !string.Equals(clan.Tag, territory.State.CapturingTag, StringComparison.OrdinalIgnoreCase))
            {
                SendReply(player, "Топливо активного генератора может трогать только захватывающий клан.");
                return false;
            }
            return null;
        }

        private object CanLootEntity(BasePlayer player, ItemBasedFlowRestrictor entity)
        {
            if (player == null || entity == null)
                return null;

            BaseEntity parent = entity.GetParentEntity();
            if (parent == null)
                return null;

            RuntimeTerritory territory = FindRuntimeByGenerator(parent.net.ID.Value);
            if (territory == null)
                return null;
            if (!CanAccessSystem(player))
                return false;

            if (_config.Capture.AllowEnemyFuelLooting)
                return null;

            ClanData clan = GetClan(player.userID);
            if (clan == null || !string.Equals(clan.Tag, territory.State.CapturingTag, StringComparison.OrdinalIgnoreCase))
            {
                SendReply(player, "Топливо активного генератора может трогать только захватывающий клан.");
                return false;
            }

            return null;
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null)
                return;

            FuelGenerator generator = entity as FuelGenerator;
            if (generator == null)
                return;

            RuntimeTerritory territory = FindRuntimeByGenerator(generator.net.ID.Value);
            if (territory == null)
                return;

            if (_config.Capture.CancelOnGeneratorDestroyed)
                CancelCapture(territory, "Генератор уничтожен — захват сорван.", true);
        }

        #endregion

        #region Commands

        [ChatCommand("clan")]
        private void ClanCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null || !CanAccessSystem(player)) return;
            UpdateKnownName(player);

            if (args == null || args.Length == 0)
            {
                OpenClanUi(player, "main");
                return;
            }

            string sub = args[0].ToLowerInvariant();
            switch (sub)
            {
                case "create":
                    if (args.Length < 2)
                    {
                        SendReply(player, "Использование: <color=#d6a84b>/clan create TAG</color>");
                        return;
                    }
                    CreateClan(player, args[1]);
                    return;

                case "cancel":
                    if (_pendingChatInputs.Remove(player.userID))
                        SendReply(player, "Ввод отменён.");
                    else
                        SendReply(player, "Сейчас нет активного ввода.");
                    return;

                case "invite":
                    if (args.Length < 2)
                    {
                        SendReply(player, "Использование: <color=#d6a84b>/clan invite имя</color>");
                        return;
                    }
                    InvitePlayer(player, string.Join(" ", args.Skip(1).ToArray()));
                    return;

                case "accept":
                    AcceptInvite(player);
                    return;

                case "decline":
                    DeclineInvite(player);
                    return;

                case "leave":
                    LeaveClan(player);
                    return;

                case "kick":
                    if (args.Length < 2) return;
                    KickMember(player, FindPlayerIdInClan(GetClan(player.userID), string.Join(" ", args.Skip(1).ToArray())));
                    return;

                case "transfer":
                    if (args.Length < 2) return;
                    TransferClan(player, FindPlayerIdInClan(GetClan(player.userID), string.Join(" ", args.Skip(1).ToArray())));
                    return;

                case "deputy":
                    if (args.Length < 2) return;
                    SetDeputy(player, GetClan(player.userID), FindPlayerIdInClan(GetClan(player.userID), string.Join(" ", args.Skip(1).ToArray())), false);
                    return;

                case "rename":
                    if (args.Length < 2) return;
                    {
                        ClanData own = GetClan(player.userID);
                        if (own != null && IsLeader(own, player.userID))
                            ChangeClanName(own, string.Join(" ", args.Skip(1).ToArray()), player, false);
                    }
                    return;

                case "retag":
                    if (args.Length < 2) return;
                    {
                        ClanData own = GetClan(player.userID);
                        if (own != null && IsLeader(own, player.userID))
                            ChangeClanTag(own, args[1], player, false);
                    }
                    return;

                case "disband":
                    DisbandClan(player);
                    return;

                case "capture":
                    StartCapture(player);
                    return;

                case "cancelcapture":
                case "stopcapture":
                    CancelOwnCapture(player);
                    return;

                case "takeover":
                case "перехват":
                    TryTakeoverNearestCapture(player);
                    return;

                case "green":
                    OpenClanUi(player, "green");
                    return;

                case "top":
                    PrintLeaderboard(player);
                    return;

                case "help":
                case "info":
                    OpenClanUi(player, "help");
                    return;

                default:
                    OpenClanUi(player, "main");
                    return;
            }
        }

        [ChatCommand("c")]
        private void ClanChatCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null || !CanAccessSystem(player) || args == null || args.Length == 0)
                return;

            ClanData clan = GetClan(player.userID);
            if (clan == null)
            {
                SendReply(player, "Вы не состоите в клане.");
                return;
            }

            string rawText = string.Join(" ", args).Trim();
            string text = EscapeRichText(rawText);
            string sender = EscapeRichText(player.displayName);
            string line = string.Format("<color={0}>[КЛАН {1}]</color> <color=#e5e5e5>{2}</color>: {3}", _config.Chat.ClanChatColor, clan.Tag, sender, text);
            if (_config.Chat.LogClanChatToServerConsole)
                Puts(string.Format("[КЛАНОВЫЙ ЧАТ] [{0}] {1} ({2}): {3}", clan.Tag, player.displayName, player.userID, rawText));
            foreach (ulong memberId in clan.Members)
            {
                BasePlayer target = BasePlayer.FindByID(memberId);
                if (target != null && target.IsConnected && CanAccessSystem(target))
                    PrintToChat(target, line);
            }
        }

        [ChatCommand("clanadmin")]
        private void ClanAdminCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null || !HasAdminPermission(player))
            {
                if (player != null)
                    SendReply(player, "Нет прав.");
                return;
            }

            if (args == null || args.Length == 0)
            {
                OpenAdminUi(player, string.Empty, "clans");
                return;
            }

            string sub = args[0].ToLowerInvariant();
            if (sub == "setpoint" && args.Length >= 2)
            {
                SetTerritoryPoint(player, args[1]);
                return;
            }

            if (sub == "cleanupstation" || sub == "cleanupstations")
            {
                int removed = CleanupOrphanCaptureStationsNear(player.transform.position, 15f);
                SendReply(player, string.Format("Удалено старых секций площадки рядом: {0}.", removed));
                return;
            }

            if (sub == "reset")
            {
                ResetSeason(false, true);
                SendReply(player, "Сезонные очки и территории сброшены. Кланы сохранены.");
                return;
            }

            if (sub == "score" && args.Length >= 3)
            {
                ClanData clan = GetClanByTag(args[1]);
                int amount;
                if (clan == null || !int.TryParse(args[2], out amount))
                {
                    SendReply(player, "Клан не найден или неверное число.");
                    return;
                }

                AdminAdjustScore(player, clan, amount, false);
                SendReply(player, string.Format("Очки [{0}]: {1}", clan.Tag, clan.Score));
                return;
            }

            if (sub == "help")
            {
                OpenAdminUi(player, string.Empty, "help");
                return;
            }

            if (sub == "stats" || sub == "statistics")
            {
                OpenAdminUi(player, string.Empty, "stats");
                return;
            }

            if (sub == "statsreset")
            {
                ResetPluginStatistics();
                SendReply(player, "Статистика RockRustClans очищена. Кланы, очки и территории не изменены.");
                OpenAdminUi(player, string.Empty, "stats");
                return;
            }

            if (sub == "status")
            {
                SendReply(player, "Доступ обычным игрокам: " + (IsPublicSystemEnabled ? "ВКЛЮЧЁН" : "ВЫКЛЮЧЕН") + ". Конфиг: Доступ к системе -> Включить систему для обычных игроков.");
                foreach (RuntimeTerritory runtime in _runtimeTerritories.Values)
                {
                    string point = runtime.Config.Calibrated && runtime.Monument != null ? FormatVector(runtime.CapturePoint) : "НЕ НАСТРОЕНА";
                    SendReply(player, string.Format("{0}: РТ найден={1}, точка настроена={2}, координаты={3}, площадка={4}, генератор={5}, владелец={6}, статус={7}",
                        runtime.Config.Id,
                        runtime.Monument != null,
                        runtime.Config.Calibrated ? "ДА" : "НЕТ",
                        point,
                        runtime.StationEntities != null && runtime.StationEntities.Count > 0 ? "ЕСТЬ" : "НЕТ",
                        runtime.Generator != null && !runtime.Generator.IsDestroyed ? "ЕСТЬ" : "НЕТ",
                        string.IsNullOrEmpty(runtime.State.OwnerTag) ? "нет" : runtime.State.OwnerTag,
                        runtime.State.Status));
                }
            }
        }

        private int ArgCount(ConsoleSystem.Arg arg)
        {
            return arg == null || arg.Args == null ? 0 : arg.Args.Length;
        }

        [ConsoleCommand("rrclan.ui")]
        private void ClanUiCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !CanAccessSystem(player) || ArgCount(arg) == 0)
                return;

            string action = arg.GetString(0, string.Empty).ToLowerInvariant();
            ulong targetId;
            switch (action)
            {
                case "begincreate":
                    BeginPendingChatInput(player, "create", null);
                    return;

                case "close":
                    CloseClanUi(player);
                    return;

                case "tab":
                    OpenClanUi(player, arg.GetString(1, "main"));
                    return;

                case "accept":
                    AcceptInvite(player);
                    OpenClanUi(player, "main");
                    return;

                case "decline":
                    DeclineInvite(player);
                    OpenClanUi(player, "main");
                    return;

                case "capture":
                    StartCapture(player);
                    OpenClanUi(player, "territory");
                    return;

                case "cancelcapture":
                    CancelOwnCapture(player);
                    ReopenClanUi(player);
                    return;

                case "takeover":
                    TryTakeoverCapture(player, arg.GetString(1, string.Empty));
                    OpenClanUi(player, "territory");
                    return;

                case "invite":
                    if (ulong.TryParse(arg.GetString(1, string.Empty), out targetId))
                    {
                        BasePlayer target = BasePlayer.FindByID(targetId);
                        if (target != null && target.IsConnected)
                            InvitePlayerDirect(player, target);
                        else
                            SendReply(player, "Игрок уже не в сети.");
                    }
                    ReopenClanUi(player);
                    return;

                case "invitename":
                {
                    string query = GetConsoleText(arg, 1);
                    if (!string.IsNullOrEmpty(query))
                        InvitePlayer(player, query);
                    ReopenClanUi(player);
                    return;
                }

                case "kick":
                    if (ulong.TryParse(arg.GetString(1, string.Empty), out targetId) && ConfirmAction(player, "kick:" + targetId))
                        KickMember(player, targetId);
                    ReopenClanUi(player);
                    return;

                case "transfer":
                    if (ulong.TryParse(arg.GetString(1, string.Empty), out targetId) && ConfirmAction(player, "transfer:" + targetId))
                        TransferClan(player, targetId);
                    ReopenClanUi(player);
                    return;

                case "leave":
                    if (ConfirmAction(player, "leave"))
                    {
                        LeaveClan(player);
                        OpenClanUi(player, "main");
                        return;
                    }
                    ReopenClanUi(player);
                    return;

                default:
                    ReopenClanUi(player);
                    return;
            }
        }

        [ConsoleCommand("rrclan.help")]
        private void ClanHelpUiCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !CanAccessSystem(player)) return;
            string section = ArgCount(arg) == 0 ? "basics" : arg.GetString(0, "basics").ToLowerInvariant();
            if (section != "basics" && section != "roles" && section != "points" && section != "capture" && section != "commands")
                section = "basics";
            _helpSections[player.userID] = section;
            OpenClanUi(player, "help");
        }

        [ConsoleCommand("rrclan.info")]
        private void ClanInfoUiCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !CanAccessSystem(player)) return;

            string section = ArgCount(arg) == 0 ? string.Empty : arg.GetString(0, string.Empty).ToLowerInvariant();
            ulong userId = player.userID;

            if (section == "close" || (section != "points" && section != "rank" && section != "captures" && section != "pvp"))
            {
                _clanInfoPanels.Remove(userId);
            }
            else
            {
                string current;
                if (_clanInfoPanels.TryGetValue(userId, out current) && current == section)
                    _clanInfoPanels.Remove(userId);
                else
                    _clanInfoPanels[userId] = section;
            }

            if (_clanUiOpen.Contains(userId))
                RefreshClanContent(player);
        }

        [ConsoleCommand("rrclan.create")]
        private void ClanCreateUiCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !CanAccessSystem(player) || ArgCount(arg) == 0)
                return;

            string tag = GetConsoleText(arg, 0);
            if (string.IsNullOrEmpty(tag))
                return;
            CreateClan(player, tag);
            OpenClanUi(player, "main");
        }

        [ConsoleCommand("rrclan.manage")]
        private void ClanManageUiCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !CanAccessSystem(player) || ArgCount(arg) == 0) return;
            ClanData clan = GetClan(player.userID);
            if (clan == null || !IsLeader(clan, player.userID)) return;

            string action = arg.GetString(0, string.Empty).ToLowerInvariant();
            ulong id;
            bool hasId = ulong.TryParse(arg.GetString(1, string.Empty), out id);
            switch (action)
            {
                case "begineditname":
                    BeginPendingChatInput(player, "leader_rename", clan);
                    return;

                case "beginedittag":
                    BeginPendingChatInput(player, "leader_retag", clan);
                    return;

                case "cancelcapture":
                    CancelOwnCapture(player);
                    break;

                case "rename":
                {
                    string value = GetConsoleText(arg, 1);
                    if (!string.IsNullOrEmpty(value))
                        ChangeClanName(clan, value, player, false);
                    break;
                }

                case "retag":
                {
                    string value = GetConsoleText(arg, 1);
                    if (!string.IsNullOrEmpty(value))
                        ChangeClanTag(clan, value, player, false);
                    break;
                }

                case "role":
                    if (hasId) CycleMemberRole(player, clan, id, false);
                    break;

                case "deputy":
                    if (hasId) SetDeputy(player, clan, id, false);
                    break;

                case "transfer":
                    if (hasId && ConfirmAction(player, "transfer:" + id))
                        TransferClan(player, id);
                    break;

                case "kick":
                    if (hasId && ConfirmAction(player, "kick:" + id))
                        KickMember(player, id);
                    break;

                case "disband":
                    if (ConfirmAction(player, "disband"))
                    {
                        DisbandClan(player);
                        OpenClanUi(player, "main");
                        return;
                    }
                    SendReply(player, "Нажмите УДАЛИТЬ КЛАН ещё раз в течение 10 секунд для подтверждения.");
                    break;
            }

            ReopenClanUi(player);
        }

        [ConsoleCommand("rrclan.green")]
        private void ClanGreenUiCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !CanAccessSystem(player) || ArgCount(arg) == 0) return;

            ClanData clan = GetClan(player.userID);
            if (clan == null || !CanManageGreenTeam(clan, player.userID)) return;

            string action = arg.GetString(0, string.Empty).ToLowerInvariant();
            ulong id;
            bool hasId = ulong.TryParse(arg.GetString(1, string.Empty), out id);
            if (action == "bind" && IsLeader(clan, player.userID))
                BindLeaderCurrentGreenTeam(player, clan);
            else if (action == "invite" && hasId)
                InviteClanMemberToGreenTeam(player, clan, id, false);
            else if (action == "remove" && hasId)
                RemoveMemberFromBoundGreenTeam(player, clan, id, false, false);
            else if (action == "restoreleader" && IsLeader(clan, player.userID))
                RestoreBoundGreenTeamLeader(player, clan, false);
            else if (action == "unbind" && IsLeader(clan, player.userID))
            {
                if (ConfirmAction(player, "unbind"))
                    UnbindGreenTeam(player, clan, false);
                else
                    SendReply(player, "Нажмите ОТВЯЗАТЬ ещё раз для подтверждения.");
            }

            OpenClanUi(player, "green");
        }

        [ConsoleCommand("rrclan.adminui")]
        private void ClanAdminUiCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAdminPermission(player)) return;

            string action = ArgCount(arg) == 0 ? "open" : arg.GetString(0, string.Empty).ToLowerInvariant();
            string mode = GetAdminMode(player);
            ulong userId = player.userID;

            switch (action)
            {
                case "close":
                    CloseAdminUi(player);
                    return;

                case "open":
                    OpenAdminUi(player, string.Empty, mode);
                    return;

                case "list":
                case "clans":
                    OpenAdminUi(player, string.Empty, "clans");
                    return;

                case "help":
                case "stats":
                case "green":
                case "territories":
                case "settings":
                    OpenAdminUi(player, string.Empty, action);
                    return;

                case "playerui":
                    CloseAdminUi(player);
                    if (CanAccessSystem(player))
                        OpenClanUi(player, "main");
                    else
                        SendReply(player, "Меню игрока сейчас скрыто: включите «Тестирование администраторами» или откройте систему для всех.");
                    return;

                case "statsreset":
                    if (ConfirmAction(player, "admin:statsreset"))
                    {
                        ResetPluginStatistics();
                        SendReply(player, "Статистика RockRustClans очищена. Кланы, очки и территории не изменены.");
                    }
                    OpenAdminUi(player, string.Empty, mode);
                    return;

                case "seasonreset":
                    if (ConfirmAction(player, "admin:seasonreset"))
                    {
                        ResetSeason(false, true);
                        SendReply(player, "Сезон сброшен: очки и территории обнулены, кланы сохранены.");
                    }
                    OpenAdminUi(player, string.Empty, mode);
                    return;

                case "rebuild":
                    ResolveCaptureStationPrefabPath();
                    BuildRuntimeTerritories();
                    SendReply(player, "Площадки точек захвата пересозданы.");
                    OpenAdminUi(player, string.Empty, mode);
                    return;

                case "search":
                {
                    string query = GetConsoleText(arg, 1);
                    if (string.IsNullOrEmpty(query))
                        _adminSearch.Remove(userId);
                    else
                        _adminSearch[userId] = query;
                    _adminPages[userId] = 0;
                    OpenAdminUi(player, string.Empty, mode == "green" ? "green" : "clans");
                    return;
                }

                case "listpage":
                    _adminPages[userId] = Math.Max(0, arg.GetInt(1, 0));
                    OpenAdminUi(player, string.Empty, mode == "green" ? "green" : "clans");
                    return;

                case "logpage":
                    _adminLogPages[userId] = Math.Max(0, arg.GetInt(1, 0));
                    OpenAdminUi(player, string.Empty, "stats");
                    return;

                case "clearsearch":
                    _adminSearch.Remove(userId);
                    _adminPages[userId] = 0;
                    OpenAdminUi(player, string.Empty, mode == "green" ? "green" : "clans");
                    return;

                case "stoggle":
                    AdminToggleSetting(player, arg.GetString(1, string.Empty));
                    OpenAdminUi(player, string.Empty, "settings");
                    return;

                case "snum":
                    AdminChangeNumber(player, arg.GetString(1, string.Empty), arg.GetInt(2, 0));
                    OpenAdminUi(player, string.Empty, "settings");
                    return;

                case "tsetpoint":
                case "ttp":
                case "tcancel":
                case "tclearowner":
                case "tclearcd":
                case "ttoggle":
                case "tgive":
                    AdminTerritoryAction(player, action, arg.GetString(1, string.Empty));
                    if (action != "ttp")
                        OpenAdminUi(player, string.Empty, "territories");
                    else
                        CloseAdminUi(player);
                    return;
            }

            string tag = arg.GetString(1, string.Empty);
            if (action == "view")
            {
                OpenAdminUi(player, tag, "clans");
                return;
            }
            if (action == "greenview")
            {
                OpenAdminUi(player, tag, "green");
                return;
            }

            ClanData clan = GetClanByTag(tag);
            if (clan == null)
            {
                OpenAdminUi(player, string.Empty, mode);
                return;
            }

            ulong id;
            bool hasId = ulong.TryParse(arg.GetString(2, string.Empty), out id);
            string returnMode = mode == "green" ? "green" : "clans";

            switch (action)
            {
                case "greenbind":
                    AdminBindOwnerGreenTeam(player, clan);
                    break;
                case "greeninvite":
                    if (hasId) InviteClanMemberToGreenTeam(player, clan, id, true);
                    break;
                case "greenforceadd":
                    if (hasId) AdminForceAddToGreenTeam(player, clan, id);
                    break;
                case "greenkick":
                    if (hasId) RemoveMemberFromBoundGreenTeam(player, clan, id, false, true);
                    break;
                case "greenleader":
                    RestoreBoundGreenTeamLeader(player, clan, true);
                    break;
                case "greenunbind":
                    if (ConfirmAction(player, "admin:unbind:" + clan.ClanId))
                        UnbindGreenTeam(player, clan, true);
                    else
                        SendReply(player, "Нажмите ОТВЯЗАТЬ ещё раз для подтверждения.");
                    break;

                case "score":
                {
                    int delta = arg.GetInt(2, 0);
                    if (delta != 0)
                        AdminAdjustScore(player, clan, delta, false);
                    break;
                }

                case "scoreinput":
                    AdminScoreInput(player, clan, GetConsoleText(arg, 2));
                    break;

                case "resetstats":
                    if (ConfirmAction(player, "admin:resetstats:" + clan.ClanId))
                    {
                        ResetClanStats(clan);
                        AddStatisticsEvent("ADMIN", clan.Tag, null, string.Format("{0} сбросил статистику клана [{1}].", player.displayName, clan.Tag));
                        SendReply(player, string.Format("Статистика клана [{0}] сброшена.", clan.Tag));
                    }
                    break;

                case "rename":
                {
                    string value = GetConsoleText(arg, 2);
                    if (!string.IsNullOrEmpty(value))
                        ChangeClanName(clan, value, player, true);
                    break;
                }

                case "retag":
                {
                    string value = GetConsoleText(arg, 2);
                    if (!string.IsNullOrEmpty(value))
                        ChangeClanTag(clan, value, player, true);
                    break;
                }

                case "editname":
                    BeginPendingChatInput(player, "admin_rename", clan);
                    return;

                case "edittag":
                    BeginPendingChatInput(player, "admin_retag", clan);
                    return;

                case "leader":
                    if (hasId) AdminSetLeader(player, clan, id);
                    break;
                case "deputy":
                    if (hasId) SetDeputy(player, clan, id, true);
                    break;
                case "role":
                    if (hasId) CycleMemberRole(player, clan, id, true);
                    break;
                case "kick":
                    if (hasId && id != clan.OwnerId && ConfirmAction(player, "admin:kick:" + id))
                        AdminKickMember(player, clan, id);
                    break;
                case "add":
                    if (hasId) AdminAddMember(player, clan, id);
                    break;

                case "delete":
                    if (ConfirmAction(player, "admin:delete:" + clan.ClanId))
                    {
                        AdminDeleteClan(clan);
                        OpenAdminUi(player, string.Empty, "clans");
                        return;
                    }
                    SendReply(player, "Нажмите УДАЛИТЬ КЛАН ещё раз для подтверждения.");
                    break;
            }

            OpenAdminUi(player, clan.Tag, returnMode);
        }

        private void AdminScoreInput(BasePlayer admin, ClanData clan, string raw)
        {
            if (admin == null || clan == null || string.IsNullOrWhiteSpace(raw))
                return;

            string text = raw.Replace(" ", string.Empty).Replace("−", "-");
            bool absolute = text.StartsWith("=");
            if (absolute)
                text = text.Substring(1);

            int value;
            if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
            {
                SendReply(admin, "Не понял число. Примеры: <color=#d6a84b>+50</color>, <color=#d6a84b>-20</color>, <color=#d6a84b>=300</color>.");
                return;
            }

            AdminAdjustScore(admin, clan, value, absolute);
        }

        #endregion

        #region Clan Core

        private ClanData GetClan(ulong playerId)
        {
            string tag;
            if (!_data.PlayerClan.TryGetValue(playerId, out tag))
                return null;
            return GetClanByTag(tag);
        }

        private ClanData GetClanByTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            ClanData clan;
            return _data.Clans.TryGetValue(tag, out clan) ? clan : null;
        }

        private bool IsLeader(ClanData clan, ulong playerId)
        {
            return clan != null && clan.OwnerId == playerId;
        }

        private bool IsDeputy(ClanData clan, ulong playerId)
        {
            if (clan == null || playerId == 0UL || clan.DeputyId != playerId)
                return false;

            RelationshipManager.PlayerTeam team = GetManualGreenTeam(clan);
            if (team != null && team.members.Contains(playerId) && team.members.Contains(clan.OwnerId))
                return true;

            clan.DeputyId = 0UL;
            SaveData();
            return false;
        }

        private bool CanManageGreenTeam(ClanData clan, ulong playerId)
        {
            return IsLeader(clan, playerId) || IsDeputy(clan, playerId);
        }

        private bool CanInviteMembers(ClanData clan, ulong playerId)
        {
            return IsLeader(clan, playerId);
        }

        private bool CanKickMember(ClanData clan, ulong actorId, ulong targetId)
        {
            return clan != null &&
                   actorId != targetId &&
                   targetId != clan.OwnerId &&
                   IsLeader(clan, actorId);
        }

        private string GetRoleName(ClanData clan, ulong playerId)
        {
            if (clan == null)
                return _config.Clan.DefaultMemberRole;

            if (clan.OwnerId == playerId)
                return _config.Clan.LeaderRoleName;
            if (IsDeputy(clan, playerId))
                return _config.Clan.DeputyRoleName;

            string role;
            if (clan.MemberRoles != null && clan.MemberRoles.TryGetValue(playerId, out role) && IsConfiguredMemberRole(role))
                return role;

            return _config.Clan.DefaultMemberRole;
        }

        private int GetRoleSort(ClanData clan, ulong playerId)
        {
            if (clan == null)
                return 100;

            if (clan.OwnerId == playerId)
                return 0;
            if (IsDeputy(clan, playerId))
                return 1;

            string role = GetRoleName(clan, playerId);
            int index = _config.Clan.MemberRoles.FindIndex(value => value.Equals(role, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? 50 : 10 + index;
        }

        private bool IsConfiguredMemberRole(string role)
        {
            return !string.IsNullOrWhiteSpace(role) &&
                   _config.Clan.MemberRoles != null &&
                   _config.Clan.MemberRoles.Any(value => value.Equals(role.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private void CycleMemberRole(BasePlayer actor, ClanData clan, ulong targetId, bool adminOverride)
        {
            if (actor == null || clan == null)
                return;

            if (!adminOverride && !IsLeader(clan, actor.userID))
                return;

            if (adminOverride && !HasAdminPermission(actor))
                return;

            if (targetId == 0UL || targetId == clan.OwnerId || !clan.Members.Contains(targetId))
                return;

            if (_config.Clan.MemberRoles == null || _config.Clan.MemberRoles.Count == 0)
                return;

            string current = GetRoleName(clan, targetId);
            int index = _config.Clan.MemberRoles.FindIndex(value => value.Equals(current, StringComparison.OrdinalIgnoreCase));
            int nextIndex = index < 0 ? 0 : (index + 1) % _config.Clan.MemberRoles.Count;
            string nextRole = _config.Clan.MemberRoles[nextIndex];

            clan.MemberRoles[targetId] = nextRole;
            SaveData();
            SendReply(actor, string.Format("Роль игрока {0}: <color=#d6a84b>{1}</color>.", GetKnownName(clan, targetId), nextRole));
        }

        private void SetDeputy(BasePlayer actor, ClanData clan, ulong targetId, bool adminOverride)
        {
            if (actor == null || clan == null || targetId == 0UL)
                return;
            if (!adminOverride && !IsLeader(clan, actor.userID))
                return;
            if (adminOverride && !HasAdminPermission(actor))
                return;
            if (targetId == clan.OwnerId || !clan.Members.Contains(targetId))
            {
                SendReply(actor, "Заместителем можно назначить только обычного участника клана.");
                return;
            }

            RelationshipManager.PlayerTeam team = GetManualGreenTeam(clan);
            if (team == null || !team.members.Contains(clan.OwnerId) || !team.members.Contains(targetId))
            {
                SendReply(actor, "Заместитель должен находиться в одной зелёной команде Rust с главой.");
                return;
            }

            if (clan.DeputyId == targetId)
            {
                clan.DeputyId = 0UL;
                SaveData();
                BroadcastToClan(clan, "Должность заместителя освобождена.");
                return;
            }

            clan.DeputyId = targetId;
            clan.MemberRoles.Remove(targetId);
            SaveData();
            BroadcastToClan(clan, string.Format("Заместитель главы: <color=#d6a84b>{0}</color>.", GetKnownName(clan, targetId)));
        }

        private void CreateClan(BasePlayer player, string rawTag)
        {
            if (player == null || !CanAccessSystem(player))
                return;

            if (GetClan(player.userID) != null)
            {
                SendReply(player, "Вы уже состоите в клане.");
                return;
            }

            RelationshipManager.PlayerTeam greenTeam = null;
            if (_config.Clan.RequireGreenTeamBeforeClanCreation)
            {
                if (player.currentTeam == 0UL || RelationshipManager.ServerInstance == null)
                {
                    SendReply(player, "Сначала создайте обычную зелёную команду Rust, затем повторите создание клана.");
                    return;
                }

                greenTeam = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);
                if (greenTeam == null || greenTeam.teamLeader != player.userID || !greenTeam.members.Contains(player.userID))
                {
                    SendReply(player, "Создать клан может только лидер своей зелёной команды Rust.");
                    return;
                }

                if (greenTeam.members.Count != 1)
                {
                    SendReply(player, "Перед созданием клана в зелёной команде должен находиться только её лидер. Участников пригласите после создания клана через меню.");
                    return;
                }

                ClanData boundClan = _data.Clans.Values.FirstOrDefault(existing => existing != null && existing.TeamId == greenTeam.teamID);
                if (boundClan != null)
                {
                    SendReply(player, "Эта зелёная команда уже привязана к другому клану.");
                    return;
                }
            }

            string tag = NormalizeTag(rawTag);
            if (tag.Length < _config.Clan.MinTagLength || tag.Length > _config.Clan.MaxTagLength || !_tagRegex.IsMatch(tag))
            {
                SendReply(player, string.Format("Префикс должен содержать {0}–{1} символов: только A-Z и 0-9.", _config.Clan.MinTagLength, _config.Clan.MaxTagLength));
                return;
            }

            if (_data.Clans.ContainsKey(tag))
            {
                SendReply(player, "Такой клан уже существует.");
                return;
            }

            ClanData clan = new ClanData
            {
                ClanId = Guid.NewGuid().ToString("N"),
                Tag = tag,
                DisplayName = tag,
                OwnerId = player.userID,
                TeamId = greenTeam == null ? 0UL : greenTeam.teamID,
                ManualTeamCreated = greenTeam != null,
                CreatedUnix = UnixNow()
            };
            clan.Members.Add(player.userID);
            clan.Names[player.userID] = player.displayName;
            clan.PlayerKills[player.userID] = 0;

            _data.Clans[tag] = clan;
            _data.PlayerClan[player.userID] = tag;
            _data.Invites.Remove(player.userID);
            SaveData();

            SendReply(player, string.Format("Клан <color={0}>[{1}]</color> создан и привязан к вашей зелёной команде Rust. Управление: <color=#d6a84b>/clan</color>.", _config.Chat.TagColor, tag));
        }

        private void InvitePlayer(BasePlayer inviter, string targetQuery)
        {
            if (inviter == null)
                return;
            BasePlayer found = FindOnlinePlayer(targetQuery, inviter);
            if (found != null)
                InvitePlayerDirect(inviter, found);
        }

        private void InvitePlayerDirect(BasePlayer inviter, BasePlayer target)
        {
            if (inviter == null || target == null)
                return;
            ClanData clan = GetClan(inviter.userID);
            if (clan == null)
            {
                SendReply(inviter, "Сначала создайте клан.");
                return;
            }
            if (!CanInviteMembers(clan, inviter.userID))
            {
                SendReply(inviter, "Недостаточно прав для приглашения игроков.");
                return;
            }
            if (clan.Members.Count >= _config.Clan.MaxMembers)
            {
                SendReply(inviter, "Клан заполнен.");
                return;
            }

            if (!target.IsConnected)
            {
                SendReply(inviter, "Игрок не в сети.");
                return;
            }
            if (!CanAccessSystem(target))
            {
                SendReply(inviter, "Система скрыта от обычных игроков. Пока доступны только администраторы с правом rockrustclans.admin.");
                return;
            }
            if (target == inviter)
            {
                SendReply(inviter, "Нельзя пригласить себя.");
                return;
            }
            if (GetClan(target.userID) != null)
            {
                SendReply(inviter, "Этот игрок уже состоит в клане.");
                return;
            }

            _data.Invites[target.userID] = new ClanInvite
            {
                ClanTag = clan.Tag,
                InviterId = inviter.userID,
                ExpiresUnix = UnixNow() + _config.Clan.InviteLifetimeSeconds
            };
            SaveData();

            SendReply(inviter, string.Format("Приглашение отправлено игроку {0}.", target.displayName));
            SendReply(target, string.Format("Вас пригласили в клан <color={0}>[{1}]</color>. Откройте <color=#d6a84b>/clan</color> или напишите <color=#d6a84b>/clan accept</color>.", _config.Chat.TagColor, clan.Tag));
        }

        private void AcceptInvite(BasePlayer player)
        {
            if (GetClan(player.userID) != null)
            {
                SendReply(player, "Вы уже состоите в клане.");
                return;
            }

            ClanInvite invite;
            if (!_data.Invites.TryGetValue(player.userID, out invite))
            {
                SendReply(player, "Активных приглашений нет.");
                return;
            }

            if (invite.ExpiresUnix < UnixNow())
            {
                _data.Invites.Remove(player.userID);
                SaveData();
                SendReply(player, "Приглашение истекло.");
                return;
            }

            ClanData clan = GetClanByTag(invite.ClanTag);
            if (clan == null || clan.Members.Count >= _config.Clan.MaxMembers)
            {
                _data.Invites.Remove(player.userID);
                SaveData();
                SendReply(player, "Клан больше недоступен или уже заполнен.");
                return;
            }

            clan.Members.Add(player.userID);
            clan.Names[player.userID] = player.displayName;
            _data.PlayerClan[player.userID] = clan.Tag;
            _data.Invites.Remove(player.userID);
            clan.MemberRoles[player.userID] = _config.Clan.DefaultMemberRole;
            if (clan.PlayerKills == null)
                clan.PlayerKills = new Dictionary<ulong, int>();
            clan.PlayerKills[player.userID] = 0;
            SaveData();

            BroadcastToClan(clan, string.Format("<color=#d6a84b>{0}</color> вступил в клан.", EscapeRichText(player.displayName)));
        }

        private void DeclineInvite(BasePlayer player)
        {
            if (_data.Invites.Remove(player.userID))
            {
                SaveData();
                SendReply(player, "Приглашение отклонено.");
            }
        }

        private void LeaveClan(BasePlayer player)
        {
            ClanData clan = GetClan(player.userID);
            if (clan == null)
            {
                SendReply(player, "Вы не состоите в клане.");
                return;
            }

            if (clan.OwnerId == player.userID)
            {
                SendReply(player, "Глава не может выйти. Передайте лидерство или распустите клан.");
                return;
            }

            RemoveMember(clan, player.userID, true);
            SendReply(player, "Вы вышли из клана.");
        }

        private void KickMember(BasePlayer actor, ulong targetId)
        {
            ClanData clan = GetClan(actor.userID);
            if (clan == null || targetId == 0UL)
                return;
            if (!CanKickMember(clan, actor.userID, targetId))
            {
                SendReply(actor, "Недостаточно прав для исключения этого участника.");
                return;
            }
            if (!clan.Members.Contains(targetId))
            {
                SendReply(actor, "Игрок не состоит в вашем клане.");
                return;
            }
            if (targetId == clan.OwnerId)
            {
                SendReply(actor, "Главу нельзя исключить.");
                return;
            }

            string name = GetKnownName(clan, targetId);
            RemoveMember(clan, targetId, true);
            SendReply(actor, string.Format("{0} исключён из клана.", name));

            BasePlayer target = BasePlayer.FindByID(targetId);
            if (target != null)
                SendReply(target, string.Format("Вы исключены из клана [{0}].", clan.Tag));
        }

        private bool ChangeClanName(ClanData clan, string newName, BasePlayer actor, bool admin)
        {
            if (clan == null) return false;
            newName = StripControlText(newName);
            if (string.IsNullOrWhiteSpace(newName) || newName.Length < 2 || newName.Length > 32)
            {
                if (actor != null) SendReply(actor, "Название клана: от 2 до 32 символов.");
                return false;
            }
            newName = newName.Trim();
            if (string.Equals(clan.DisplayName, newName, StringComparison.Ordinal))
                return true;
            if (!admin && clan.NameChangedThisWipe)
            {
                if (actor != null) SendReply(actor, "Название клана уже меняли на этом вайпе. Следующая смена будет доступна после смены карты.");
                return false;
            }

            clan.DisplayName = newName;
            if (!admin)
                clan.NameChangedThisWipe = true;
            SaveData();
            BroadcastToClan(clan, "Название клана изменено на <color=#d6a84b>" + EscapeRichText(clan.DisplayName) + "</color>.");
            return true;
        }

        private bool ChangeClanTag(ClanData clan, string rawTag, BasePlayer actor, bool admin)
        {
            if (clan == null) return false;
            string newTag = NormalizeTag(rawTag);
            if (newTag.Length < _config.Clan.MinTagLength || newTag.Length > _config.Clan.MaxTagLength || !_tagRegex.IsMatch(newTag))
            {
                if (actor != null) SendReply(actor, string.Format("Префикс должен содержать {0}–{1} символов A-Z/0-9.", _config.Clan.MinTagLength, _config.Clan.MaxTagLength));
                return false;
            }
            if (string.Equals(clan.Tag, newTag, StringComparison.OrdinalIgnoreCase)) return true;
            if (!admin && clan.TagChangedThisWipe)
            {
                if (actor != null) SendReply(actor, "Тег клана уже меняли на этом вайпе. Следующая смена будет доступна после смены карты.");
                return false;
            }
            if (_data.Clans.ContainsKey(newTag))
            {
                if (actor != null) SendReply(actor, "Такой тег уже занят.");
                return false;
            }

            string oldTag = clan.Tag;
            _data.Clans.Remove(oldTag);
            clan.Tag = newTag;
            if (!admin)
                clan.TagChangedThisWipe = true;
            clan.LastTagChangeUnix = UnixNow();
            _data.Clans[newTag] = clan;

            foreach (ulong memberId in clan.Members) _data.PlayerClan[memberId] = newTag;
            foreach (ClanInvite invite in _data.Invites.Values)
                if (invite != null && string.Equals(invite.ClanTag, oldTag, StringComparison.OrdinalIgnoreCase)) invite.ClanTag = newTag;
            foreach (TerritoryState state in _data.Territories.Values)
            {
                if (string.Equals(state.OwnerTag, oldTag, StringComparison.OrdinalIgnoreCase)) state.OwnerTag = newTag;
                if (string.Equals(state.CapturingTag, oldTag, StringComparison.OrdinalIgnoreCase)) state.CapturingTag = newTag;
            }
            if (string.Equals(_data.LastWinnerTag, oldTag, StringComparison.OrdinalIgnoreCase)) _data.LastWinnerTag = newTag;
            foreach (ulong viewer in _adminSelectedClans.Where(x => string.Equals(x.Value, oldTag, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key).ToList())
                _adminSelectedClans[viewer] = newTag;

            SaveData();
            AnnounceSystem(string.Format("<color=#d6a84b>[КЛАНЫ]</color> Клан [{0}] сменил тег на <color={1}>[{2}]</color>.", oldTag, _config.Chat.TagColor, newTag));
            return true;
        }

        private string StripControlText(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            value = Regex.Replace(value, "<.*?>", string.Empty);
            value = value.Replace("\\n", " ").Replace("\\r", " ").Replace("\n", " ").Replace("\r", " ");
            value = Regex.Replace(value, @"[^\p{L}\p{N} _\-.]", string.Empty).Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value;
        }

        private void TransferClan(BasePlayer actor, ulong targetId)
        {
            ClanData clan = GetClan(actor.userID);
            if (clan == null || targetId == 0UL) return;

            if (!IsLeader(clan, actor.userID))
            {
                SendReply(actor, "Только глава может передать лидерство.");
                return;
            }

            if (!clan.Members.Contains(targetId) || targetId == clan.OwnerId)
                return;

            RelationshipManager.PlayerTeam team = GetManualGreenTeam(clan);
            if (team == null || !team.members.Contains(targetId) || !team.members.Contains(clan.OwnerId))
            {
                SendReply(actor, "Новый глава должен находиться в привязанной зелёной команде вместе с текущим главой.");
                return;
            }

            ulong oldOwner = clan.OwnerId;
            team.SetTeamLeader(targetId);
            clan.OwnerId = targetId;
            clan.MemberRoles.Remove(targetId);
            if (clan.DeputyId == targetId)
                clan.DeputyId = 0UL;

            if (oldOwner != 0UL && clan.Members.Contains(oldOwner))
                clan.MemberRoles[oldOwner] = _config.Clan.DefaultMemberRole;

            SaveData();
            BroadcastToClan(clan, string.Format("Новый глава клана: <color=#d6a84b>{0}</color>.", GetKnownName(clan, targetId)));
        }

        private void DisbandClan(BasePlayer actor)
        {
            ClanData clan = GetClan(actor.userID);
            if (clan == null) return;
            if (!IsLeader(clan, actor.userID))
            {
                SendReply(actor, "Распустить клан может только глава.");
                return;
            }

            string tag = clan.Tag;
            List<ulong> formerMembers = clan.Members.ToList();

            CancelCapturesByClan(tag, "Клан распущен — захват отменён.");
            ClearTerritoryOwnership(tag);
            ClearGreenTeamBinding(clan);

            foreach (ulong member in formerMembers)
            {
                _data.PlayerClan.Remove(member);

                BasePlayer onlineMember = BasePlayer.FindByID(member);
                if (onlineMember == null)
                    continue;

                CuiHelper.DestroyUi(onlineMember, UiRoot);
                _clanUiOpen.Remove(member);
                _openTabs.Remove(member);
                _clanInfoPanels.Remove(member);
                _clanNavSignatures.Remove(member);
                _clanHeaderSignatures.Remove(member);
                _helpSections.Remove(member);
                _pendingChatInputs.Remove(member);
                _leaderDisbandConfirmUntil.Remove(member);
            }

            _data.Clans.Remove(tag);
            foreach (ulong target in _data.Invites.Where(x => x.Value != null && string.Equals(x.Value.ClanTag, tag, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key).ToList())
                _data.Invites.Remove(target);

            SaveData();
            AnnounceSystem(string.Format("Клан <color={0}>[{1}]</color> распущен.", _config.Chat.TagColor, tag));
        }

        private void RemoveMember(ClanData clan, ulong memberId, bool save)
        {
            if (clan == null)
                return;

            if (_config.Clan.RemoveFromGreenTeamOnLeave && memberId != clan.OwnerId)
            {
                RelationshipManager.PlayerTeam team = GetManualGreenTeam(clan);
                if (team != null && team.members.Contains(memberId))
                    team.RemovePlayer(memberId);
            }

            clan.Members.Remove(memberId);
            clan.MemberRoles.Remove(memberId);
            if (clan.PlayerKills != null)
                clan.PlayerKills.Remove(memberId);
            if (clan.DeputyId == memberId)
                clan.DeputyId = 0UL;
            _data.PlayerClan.Remove(memberId);

            if (save)
                SaveData();

            CloseClanUiFor(memberId);
            BroadcastToClan(clan, string.Format("{0} больше не состоит в клане.", GetKnownName(clan, memberId)));
        }

        private void UpdateKnownName(BasePlayer player)
        {
            ClanData clan = GetClan(player.userID);
            if (clan == null) return;
            clan.Names[player.userID] = player.displayName;
        }

        private void AdminSetLeader(BasePlayer admin, ClanData clan, ulong targetId)
        {
            if (clan == null || targetId == 0UL || !clan.Members.Contains(targetId))
                return;
            if (targetId == clan.OwnerId)
            {
                if (admin != null) SendReply(admin, "Этот игрок уже глава клана.");
                return;
            }

            RelationshipManager.PlayerTeam team = GetManualGreenTeam(clan);
            ulong oldOwner = clan.OwnerId;
            bool teamSynced = false;
            if (team != null && team.members.Contains(targetId))
            {
                team.SetTeamLeader(targetId);
                teamSynced = true;
            }

            clan.OwnerId = targetId;
            clan.MemberRoles.Remove(targetId);
            if (clan.DeputyId == targetId)
                clan.DeputyId = 0UL;
            if (oldOwner != 0UL && clan.Members.Contains(oldOwner))
                clan.MemberRoles[oldOwner] = _config.Clan.DefaultMemberRole;

            SaveData();
            BroadcastToClan(clan, string.Format("Администратор назначил нового главу клана: <color=#d6a84b>{0}</color>.", EscapeRichText(GetKnownName(clan, targetId))));
            if (admin != null)
            {
                SendReply(admin, string.Format("Новый глава [{0}]: {1}.", clan.Tag, GetKnownName(clan, targetId)));
                if (team != null && !teamSynced)
                    SendReply(admin, "Внимание: новый глава не состоит в привязанной зелёной команде. Добавьте его во вкладке ЗЕЛЁНКА и нажмите ВЕРНУТЬ ЛИДЕРА.");
            }
        }

        private void AdminKickMember(BasePlayer admin, ClanData clan, ulong targetId)
        {
            if (clan == null || targetId == 0UL || targetId == clan.OwnerId || !clan.Members.Contains(targetId))
                return;
            string name = GetKnownName(clan, targetId);
            RemoveMember(clan, targetId, true);
            if (admin != null)
                SendReply(admin, string.Format("{0} исключён из [{1}].", name, clan.Tag));
            BasePlayer target = BasePlayer.FindByID(targetId);
            if (target != null && target.IsConnected)
                SendReply(target, string.Format("Администратор исключил вас из клана [{0}].", clan.Tag));
        }

        private void AdminAddMember(BasePlayer admin, ClanData clan, ulong targetId)
        {
            if (clan == null || targetId == 0UL) return;

            if (clan.Members.Count >= _config.Clan.MaxMembers)
            {
                SendReply(admin, "Клан уже заполнен.");
                return;
            }

            if (GetClan(targetId) != null)
            {
                SendReply(admin, "Этот игрок уже состоит в другом клане.");
                return;
            }

            BasePlayer target = BasePlayer.FindByID(targetId);
            if (target == null || !target.IsConnected)
            {
                SendReply(admin, "Через GUI можно добавить только игрока, который сейчас онлайн.");
                return;
            }

            if (!CanAccessSystem(target))
            {
                SendReply(admin, "Система сейчас скрыта. Добавлять можно только других администраторов-тестировщиков.");
                return;
            }

            clan.Members.Add(targetId);
            clan.Names[targetId] = target.displayName;
            clan.MemberRoles[targetId] = _config.Clan.DefaultMemberRole;
            if (clan.PlayerKills == null)
                clan.PlayerKills = new Dictionary<ulong, int>();
            clan.PlayerKills[targetId] = 0;
            _data.PlayerClan[targetId] = clan.Tag;
            SaveData();

            BroadcastToClan(clan, string.Format("Администратор добавил <color=#d6a84b>{0}</color> в клан.", EscapeRichText(target.displayName)));
        }

        private void AdminDeleteClan(ClanData clan)
        {
            if (clan == null) return;
            string tag = clan.Tag;
            List<ulong> formerMembers = clan.Members.ToList();

            CancelCapturesByClan(tag, "Клан удалён администратором — захват отменён.");
            ClearTerritoryOwnership(tag);
            ClearGreenTeamBinding(clan);

            foreach (ulong member in formerMembers)
            {
                _data.PlayerClan.Remove(member);

                BasePlayer onlineMember = BasePlayer.FindByID(member);
                if (onlineMember == null)
                    continue;

                CuiHelper.DestroyUi(onlineMember, UiRoot);
                _clanUiOpen.Remove(member);
                _openTabs.Remove(member);
                _clanInfoPanels.Remove(member);
                _clanNavSignatures.Remove(member);
                _clanHeaderSignatures.Remove(member);
                _helpSections.Remove(member);
                _pendingChatInputs.Remove(member);
                _leaderDisbandConfirmUntil.Remove(member);
            }

            _data.Clans.Remove(tag);
            foreach (ulong target in _data.Invites.Where(x => x.Value != null && string.Equals(x.Value.ClanTag, tag, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key).ToList())
                _data.Invites.Remove(target);
            SaveData();
            AnnounceSystem(string.Format("<color=#d6a84b>[КЛАНЫ]</color> Клан <color={0}>[{1}]</color> удалён администратором.", _config.Chat.TagColor, tag));
        }

        #endregion

        #region Привязанная зелёная команда Rust

        private RelationshipManager.PlayerTeam GetManualGreenTeam(ClanData clan)
        {
            if (clan == null || clan.TeamId == 0UL || RelationshipManager.ServerInstance == null)
                return null;

            RelationshipManager.PlayerTeam team = RelationshipManager.ServerInstance.FindTeam(clan.TeamId);
            if (team == null)
            {
                ClearGreenTeamBinding(clan);
                SaveData();
            }
            return team;
        }

        private bool IsInManualGreenTeam(ClanData clan, ulong playerId)
        {
            RelationshipManager.PlayerTeam team = GetManualGreenTeam(clan);
            return team != null && team.members.Contains(playerId);
        }

        private ClanData GetClanByTeamId(ulong teamId)
        {
            if (teamId == 0UL)
                return null;
            return _data.Clans.Values.FirstOrDefault(clan => clan != null && clan.TeamId == teamId);
        }

        private void ClearGreenTeamBinding(ClanData clan)
        {
            if (clan == null)
                return;
            clan.TeamId = 0UL;
            clan.ManualTeamCreated = false;
            clan.DeputyId = 0UL;
        }

        private void BindLeaderCurrentGreenTeam(BasePlayer actor, ClanData clan)
        {
            if (actor == null || clan == null || !IsLeader(clan, actor.userID) || RelationshipManager.ServerInstance == null)
                return;
            if (actor.currentTeam == 0UL)
            {
                SendReply(actor, "Сначала создайте обычную зелёную команду Rust.");
                return;
            }

            RelationshipManager.PlayerTeam team = RelationshipManager.ServerInstance.FindTeam(actor.currentTeam);
            if (team == null || team.teamLeader != actor.userID || !team.members.Contains(actor.userID))
            {
                SendReply(actor, "Привязать можно только зелёную команду, лидером которой вы являетесь.");
                return;
            }

            ClanData other = GetClanByTeamId(team.teamID);
            if (other != null && other.ClanId != clan.ClanId)
            {
                SendReply(actor, "Эта зелёная команда уже привязана к другому клану.");
                return;
            }

            clan.TeamId = team.teamID;
            clan.ManualTeamCreated = true;
            if (clan.DeputyId != 0UL && !team.members.Contains(clan.DeputyId))
                clan.DeputyId = 0UL;
            SaveData();
            SendReply(actor, "Привязана именно ваша текущая зелёная команда Rust.");
        }

        private void InviteClanMemberToGreenTeam(BasePlayer actor, ClanData clan, ulong targetId, bool adminOverride)
        {
            if (actor == null || clan == null || targetId == 0UL)
                return;
            if (!adminOverride && !CanManageGreenTeam(clan, actor.userID))
                return;
            if (adminOverride && !HasAdminPermission(actor))
                return;
            if (!clan.Members.Contains(targetId))
            {
                SendReply(actor, "Игрок не состоит в этом клане.");
                return;
            }

            RelationshipManager.PlayerTeam team = GetManualGreenTeam(clan);
            if (team == null)
            {
                SendReply(actor, "У клана нет привязанной зелёной команды.");
                return;
            }
            if (team.teamLeader != clan.OwnerId || !team.members.Contains(clan.OwnerId))
            {
                SendReply(actor, "Глава клана должен быть лидером привязанной зелёной команды. Исправьте лидера в админ-панели или обычном меню Rust.");
                return;
            }
            if (!adminOverride && IsDeputy(clan, actor.userID) && !team.members.Contains(actor.userID))
            {
                SendReply(actor, "Заместитель должен находиться в зелёной команде вместе с главой.");
                return;
            }
            if (team.members.Contains(targetId))
            {
                SendReply(actor, "Игрок уже находится в зелёной команде клана.");
                return;
            }
            if (team.members.Count >= 8)
            {
                SendReply(actor, "Зелёная команда Rust уже заполнена.");
                return;
            }

            BasePlayer target = BasePlayer.FindAwakeOrSleepingByID(targetId);
            if (target != null && target.currentTeam != 0UL && target.currentTeam != team.teamID)
            {
                SendReply(actor, "Игрок уже находится в другой зелёной команде. Сначала он должен выйти из неё.");
                return;
            }

            team.SendInvite(target, targetId);
            SendReply(actor, string.Format("Игроку {0} отправлено приглашение в зелёную команду. Он должен принять его в обычном интерфейсе Rust.", GetKnownName(clan, targetId)));
            if (target != null && target.IsConnected)
                SendReply(target, string.Format("Глава или заместитель клана [{0}] пригласил вас в зелёную команду Rust. Примите обычное командное приглашение.", clan.Tag));
        }

        private void AdminForceAddToGreenTeam(BasePlayer admin, ClanData clan, ulong targetId)
        {
            if (admin == null || !HasAdminPermission(admin) || clan == null || !clan.Members.Contains(targetId))
                return;

            RelationshipManager.PlayerTeam team = GetManualGreenTeam(clan);
            if (team == null)
            {
                SendReply(admin, "У клана нет привязанной зелёной команды.");
                return;
            }
            if (team.members.Contains(targetId))
            {
                SendReply(admin, "Игрок уже находится в этой зелёной команде.");
                return;
            }
            if (team.members.Count >= 8)
            {
                SendReply(admin, "Зелёная команда Rust уже заполнена.");
                return;
            }

            BasePlayer target = BasePlayer.FindByID(targetId);
            if (target == null || !target.IsConnected)
            {
                SendReply(admin, "Принудительно добавить можно только игрока онлайн. Для остальных используйте приглашение.");
                return;
            }
            if (target.currentTeam != 0UL && target.currentTeam != team.teamID)
            {
                SendReply(admin, "Игрок находится в другой зелёной команде.");
                return;
            }

            team.AddPlayer(target);
            SendReply(admin, string.Format("{0} принудительно добавлен в зелёную команду клана.", GetKnownName(clan, targetId)));
        }

        private void RemoveMemberFromBoundGreenTeam(BasePlayer actor, ClanData clan, ulong targetId, bool silent, bool adminOverride)
        {
            if (clan == null || targetId == 0UL)
                return;
            if (actor != null && !adminOverride && !CanManageGreenTeam(clan, actor.userID))
                return;
            if (actor != null && adminOverride && !HasAdminPermission(actor))
                return;
            if (targetId == clan.OwnerId)
            {
                if (!silent && actor != null) SendReply(actor, "Главу нельзя удалить из привязанной зелёной команды. Сначала отвяжите команду или смените главу.");
                return;
            }

            RelationshipManager.PlayerTeam team = GetManualGreenTeam(clan);
            if (team == null || !team.members.Contains(targetId))
            {
                if (!silent && actor != null) SendReply(actor, "Игрок не находится в зелёной команде клана.");
                return;
            }

            team.RemovePlayer(targetId);
            if (clan.DeputyId == targetId)
                clan.DeputyId = 0UL;
            SaveData();
            if (!silent && actor != null)
                SendReply(actor, string.Format("{0} удалён из зелёной команды клана.", GetKnownName(clan, targetId)));
        }

        private void RestoreBoundGreenTeamLeader(BasePlayer actor, ClanData clan, bool adminOverride)
        {
            if (actor == null || clan == null)
                return;
            if (!adminOverride && !IsLeader(clan, actor.userID))
                return;
            if (adminOverride && !HasAdminPermission(actor))
                return;

            RelationshipManager.PlayerTeam team = GetManualGreenTeam(clan);
            if (team == null || !team.members.Contains(clan.OwnerId))
            {
                SendReply(actor, "Глава клана отсутствует в привязанной зелёной команде.");
                return;
            }

            team.SetTeamLeader(clan.OwnerId);
            SendReply(actor, "Глава клана назначен лидером привязанной зелёной команды Rust.");
        }

        private void UnbindGreenTeam(BasePlayer actor, ClanData clan, bool adminOverride)
        {
            if (actor == null || clan == null)
                return;
            if (!adminOverride && !IsLeader(clan, actor.userID))
                return;
            if (adminOverride && !HasAdminPermission(actor))
                return;

            ClearGreenTeamBinding(clan);
            SaveData();
            SendReply(actor, "Зелёная команда отвязана от клана. Сама обычная команда Rust не распущена.");
        }

        private void AdminBindOwnerGreenTeam(BasePlayer admin, ClanData clan)
        {
            if (admin == null || !HasAdminPermission(admin) || clan == null || RelationshipManager.ServerInstance == null)
                return;

            BasePlayer owner = BasePlayer.FindByID(clan.OwnerId);
            if (owner == null || !owner.IsConnected || owner.currentTeam == 0UL)
            {
                SendReply(admin, "Глава клана должен быть онлайн и находиться в своей зелёной команде.");
                return;
            }

            RelationshipManager.PlayerTeam team = RelationshipManager.ServerInstance.FindTeam(owner.currentTeam);
            if (team == null || team.teamLeader != owner.userID || !team.members.Contains(owner.userID))
            {
                SendReply(admin, "Глава не является лидером своей текущей зелёной команды.");
                return;
            }

            ClanData other = GetClanByTeamId(team.teamID);
            if (other != null && other.ClanId != clan.ClanId)
            {
                SendReply(admin, "Эта зелёная команда уже привязана к другому клану.");
                return;
            }

            clan.TeamId = team.teamID;
            clan.ManualTeamCreated = true;
            if (clan.DeputyId != 0UL && !team.members.Contains(clan.DeputyId))
                clan.DeputyId = 0UL;
            SaveData();
            SendReply(admin, "Текущая зелёная команда главы привязана к клану.");
        }

        #endregion

        #region Territory Runtime

        private class RuntimeTerritory
        {
            public TerritoryConfig Config;
            public TerritoryState State;
            public MonumentInfo Monument;
            public Vector3 CapturePoint;
            public FuelGenerator Generator;
            public List<BaseEntity> StationEntities = new List<BaseEntity>();
        }

        private void ResolveGeneratorPrefabPath()
        {
            _generatorPrefabPath = string.Empty;
            try
            {
                ItemDefinition definition = ItemManager.FindItemDefinition("electric.fuelgenerator.small");
                ItemModDeployable deployable = definition == null ? null : definition.GetComponent<ItemModDeployable>();
                if (deployable != null && deployable.entityPrefab != null && !string.IsNullOrEmpty(deployable.entityPrefab.resourcePath))
                    _generatorPrefabPath = deployable.entityPrefab.resourcePath;
            }
            catch (Exception ex)
            {
                PrintWarning("Не удалось определить prefab Small Generator через ItemDefinition: " + ex.Message);
            }
            if (string.IsNullOrEmpty(_generatorPrefabPath))
            {
                PrintError("Не удалось определить prefab настоящего Small Generator (electric.fuelgenerator.small). Захваты будут недоступны до исправления, старый Test Generator использоваться не будет.");
                return;
            }

            Puts("Small Generator prefab: " + _generatorPrefabPath);
        }

        private void ResolveCaptureStationPrefabPath()
        {
            _captureStationPrefabPath = string.Empty;
            if (!_config.Capture.ShowCaptureStation)
                return;

            try
            {
                ItemDefinition definition = ItemManager.FindItemDefinition(_config.Capture.CaptureStationDeployableShortname);
                ItemModDeployable deployable = definition == null ? null : definition.GetComponent<ItemModDeployable>();
                if (deployable != null && deployable.entityPrefab != null && !string.IsNullOrEmpty(deployable.entityPrefab.resourcePath))
                    _captureStationPrefabPath = deployable.entityPrefab.resourcePath;
            }
            catch (Exception ex)
            {
                PrintWarning("Не удалось определить prefab ограждения площадки захвата: " + ex.Message);
            }

            if (_config.Capture.ShowCaptureStation && string.IsNullOrEmpty(_captureStationPrefabPath))
                PrintWarning("Не найден deployable для площадки захвата по shortname '" + _config.Capture.CaptureStationDeployableShortname + "'. Площадка не будет создана, но сам захват продолжит работать.");
        }

        private void BuildRuntimeTerritories()
        {
            Dictionary<string, FuelGenerator> existingGenerators = new Dictionary<string, FuelGenerator>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, RuntimeTerritory> pair in _runtimeTerritories)
            {
                if (pair.Value != null)
                {
                    if (pair.Value.Generator != null && !pair.Value.Generator.IsDestroyed)
                        existingGenerators[pair.Key] = pair.Value.Generator;
                    KillCaptureStation(pair.Value);
                }
            }

            _runtimeTerritories.Clear();
            _captureStationNetIds.Clear();

            foreach (TerritoryConfig config in _config.Territories.Where(t => t.Enabled))
            {
                TerritoryPointData pointData;
                if (_data != null && _data.CapturePoints != null && _data.CapturePoints.TryGetValue(config.Id, out pointData) && pointData != null && pointData.Calibrated && pointData.LocalOffset != null)
                {
                    config.LocalOffset = pointData.LocalOffset;
                    config.Calibrated = true;
                }

                TerritoryState state;
                if (!_data.Territories.TryGetValue(config.Id, out state))
                {
                    state = new TerritoryState { Id = config.Id };
                    _data.Territories[config.Id] = state;
                }

                MonumentInfo monument = FindMonument(config.PrefabContains);
                RuntimeTerritory runtime = new RuntimeTerritory
                {
                    Config = config,
                    State = state,
                    Monument = monument,
                    CapturePoint = monument == null ? Vector3.zero : ResolveCapturePoint(config, monument)
                };

                FuelGenerator oldGenerator;
                if (existingGenerators.TryGetValue(config.Id, out oldGenerator))
                    runtime.Generator = oldGenerator;

                _runtimeTerritories[config.Id] = runtime;

                if (monument == null)
                {
                    PrintWarning(string.Format("РТ '{0}' не найден по шаблону '{1}'.", config.Id, config.PrefabContains));
                    continue;
                }

                if (!config.Calibrated)
                {
                    PrintWarning(string.Format("РТ '{0}' найден, но точка генератора не откалибрована. Встаньте в нужное место и выполните /clanadmin setpoint {0}", config.Id));
                    continue;
                }

                // Убираем «осиротевшие» мешки, оставшиеся от дублей территорий в старых версиях.
                int orphans = CleanupOrphanCaptureStationsNear(runtime.CapturePoint, _config.Capture.CaptureStationRadius + 4f);
                if (orphans > 0)
                    Puts(string.Format("Удалено лишних секций площадки у «{0}»: {1}.", config.DisplayName, orphans));
                SpawnCaptureStation(runtime);
            }
        }

        private MonumentInfo FindMonument(string contains)
        {
            if (TerrainMeta.Path == null || TerrainMeta.Path.Monuments == null || string.IsNullOrEmpty(contains))
                return null;

            string needle = contains.ToLowerInvariant();
            return TerrainMeta.Path.Monuments.FirstOrDefault(m =>
                m != null && !string.IsNullOrEmpty(m.name) && m.name.ToLowerInvariant().Contains(needle));
        }

        private Vector3 ResolveCapturePoint(TerritoryConfig config, MonumentInfo monument)
        {
            Vector3 position = config.Calibrated
                ? monument.transform.TransformPoint(config.LocalOffset.ToVector3())
                : monument.transform.position;

            if (!config.Calibrated && TerrainMeta.HeightMap != null)
            {
                float terrainY = TerrainMeta.HeightMap.GetHeight(position);
                if (position.y < terrainY + 0.2f)
                    position.y = terrainY + 0.2f;
            }

            return position;
        }

        private void SetTerritoryPoint(BasePlayer admin, string id)
        {
            TerritoryConfig config = _config.Territories.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            if (config == null)
            {
                SendReply(admin, "Неизвестный ID. Доступно: " + string.Join(", ", _config.Territories.Select(t => t.Id).ToArray()));
                return;
            }

            RuntimeTerritory currentRuntime;
            if (_runtimeTerritories.TryGetValue(config.Id, out currentRuntime) && currentRuntime != null && currentRuntime.State != null && !string.IsNullOrEmpty(currentRuntime.State.CapturingTag))
            {
                SendReply(admin, "Нельзя переносить точку во время активного захвата. Сначала дождитесь завершения или сбросьте сезон.");
                return;
            }

            MonumentInfo monument = FindNearestMatchingMonument(admin.transform.position, config.PrefabContains);
            if (monument == null)
            {
                SendReply(admin, "Не найден подходящий монумент рядом. Проверьте поле «Часть названия prefab монумента» в конфиге.");
                return;
            }

            float monumentDistance = Vector3.Distance(admin.transform.position, monument.transform.position);
            float maxMonumentDistance = Mathf.Max(config.Radius * 2f, 250f);
            if (monumentDistance > maxMonumentDistance)
            {
                SendReply(admin, string.Format("Вы слишком далеко от монумента «{0}» ({1:0} м). Встаньте на сам РТ в место, где должен стоять генератор.", config.DisplayName, monumentDistance));
                return;
            }

            // Сначала удаляем старую площадку именно у прежней точки, чтобы при переносе
            // не оставались сиротские секции от предыдущей калибровки.
            if (currentRuntime != null)
            {
                KillCaptureStation(currentRuntime);
                if (currentRuntime.Config != null && currentRuntime.Config.Calibrated && currentRuntime.Monument != null)
                    CleanupOrphanCaptureStationsNear(currentRuntime.CapturePoint, 12f);
            }

            Vector3 local = monument.transform.InverseTransformPoint(admin.transform.position);
            SerializableVector3 storedOffset = SerializableVector3.FromVector3(local);
            config.LocalOffset = storedOffset;
            config.Calibrated = true;
            config.Enabled = true;

            if (_data.CapturePoints == null)
                _data.CapturePoints = new Dictionary<string, TerritoryPointData>(StringComparer.OrdinalIgnoreCase);
            _data.CapturePoints[config.Id] = new TerritoryPointData
            {
                Calibrated = true,
                LocalOffset = SerializableVector3.FromVector3(local)
            };

            SaveConfig();
            SaveData();
            ResolveCaptureStationPrefabPath();
            BuildRuntimeTerritories();

            RuntimeTerritory runtime;
            bool ready = _runtimeTerritories.TryGetValue(config.Id, out runtime) && runtime != null && runtime.Monument != null && runtime.Config.Calibrated;
            SendReply(admin, string.Format("Точка <color=#d6a84b>{0}</color> сохранена. Настроена: <b>{1}</b>. Координаты: {2}. Площадка захвата создана сразу; сам генератор появится только когда глава клана начнёт захват.", config.DisplayName, ready ? "ДА" : "НЕТ", ready ? FormatVector(runtime.CapturePoint) : "не определены"));
        }

        private MonumentInfo FindNearestMatchingMonument(Vector3 position, string contains)
        {
            if (TerrainMeta.Path == null || TerrainMeta.Path.Monuments == null)
                return null;

            string needle = (contains ?? string.Empty).ToLowerInvariant();
            return TerrainMeta.Path.Monuments
                .Where(m => m != null && !string.IsNullOrEmpty(m.name) && m.name.ToLowerInvariant().Contains(needle))
                .OrderBy(m => Vector3.Distance(position, m.transform.position))
                .FirstOrDefault();
        }

        private bool SpawnCaptureStation(RuntimeTerritory territory)
        {
            if (territory == null || territory.Monument == null || !territory.Config.Calibrated)
                return false;

            KillCaptureStation(territory);
            if (!_config.Capture.ShowCaptureStation)
                return true;
            if (string.IsNullOrEmpty(_captureStationPrefabPath))
                ResolveCaptureStationPrefabPath();
            if (string.IsNullOrEmpty(_captureStationPrefabPath))
                return false;

            float radius = _config.Capture.CaptureStationRadius;
            // Пять секций образуют заметную U-образную площадку с открытым входом.
            float[] angles = { 70f, 125f, 180f, 235f, 290f };
            Quaternion monumentRotation = territory.Monument.transform.rotation;

            for (int i = 0; i < angles.Length; i++)
            {
                float angle = angles[i];
                float radians = angle * Mathf.Deg2Rad;
                Vector3 localOffset = new Vector3(Mathf.Cos(radians) * radius, 0f, Mathf.Sin(radians) * radius);
                Vector3 worldOffset = monumentRotation * localOffset;
                Vector3 position = territory.CapturePoint + worldOffset;
                Quaternion rotation = monumentRotation * Quaternion.Euler(0f, 90f - angle, 0f);

                BaseEntity entity = GameManager.server.CreateEntity(_captureStationPrefabPath, position, rotation, true);
                if (entity == null)
                {
                    PrintWarning("Не удалось создать одну из секций площадки захвата " + territory.Config.Id + ".");
                    continue;
                }

                entity.OwnerID = 0UL;
                entity.enableSaving = false;
                entity.Spawn();

                BaseCombatEntity combat = entity as BaseCombatEntity;
                if (combat != null)
                {
                    combat._maxHealth = Mathf.Max(combat._maxHealth, 10000f);
                    combat.health = combat._maxHealth;
                }

                territory.StationEntities.Add(entity);
                if (entity.net != null)
                    _captureStationNetIds.Add(entity.net.ID.Value);
            }

            return territory.StationEntities.Count > 0;
        }

        private void KillCaptureStation(RuntimeTerritory territory)
        {
            if (territory == null || territory.StationEntities == null)
                return;

            foreach (BaseEntity entity in territory.StationEntities)
            {
                if (entity == null)
                    continue;
                if (entity.net != null)
                    _captureStationNetIds.Remove(entity.net.ID.Value);
                if (!entity.IsDestroyed)
                    entity.Kill();
            }
            territory.StationEntities.Clear();
        }

        private int CleanupOrphanCaptureStationsNear(Vector3 position, float radius)
        {
            if (string.IsNullOrEmpty(_captureStationPrefabPath))
                ResolveCaptureStationPrefabPath();
            if (string.IsNullOrEmpty(_captureStationPrefabPath))
                return 0;

            int removed = 0;
            List<BaseEntity> toKill = new List<BaseEntity>();
            foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
            {
                BaseEntity entity = networkable as BaseEntity;
                if (entity == null || entity.IsDestroyed)
                    continue;
                if (entity.enableSaving)
                    continue;
                if (entity.OwnerID != 0UL)
                    continue;
                if (!string.Equals(entity.PrefabName, _captureStationPrefabPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (Vector3.Distance(entity.transform.position, position) > radius)
                    continue;

                toKill.Add(entity);
            }

            foreach (BaseEntity entity in toKill)
            {
                if (entity == null || entity.IsDestroyed)
                    continue;
                if (entity.net != null)
                    _captureStationNetIds.Remove(entity.net.ID.Value);
                entity.Kill();
                removed++;
            }

            return removed;
        }

        private void StartTimers()
        {
            if (_captureTimer != null) _captureTimer.Destroy();
            if (_dailyTimer != null) _dailyTimer.Destroy();

            _captureTimer = timer.Every(_config.Capture.TickSeconds, CaptureTick);
            if (_saveTimer != null) _saveTimer.Destroy();
            _saveTimer = timer.Every(10f, () =>
            {
                if (_dataDirty)
                    FlushData();
            });
            _dailyTimer = timer.Every(60f, () =>
            {
                CleanupInvites();
            });
        }

        private void StartCapture(BasePlayer leader)
        {
            if (leader == null || !CanAccessSystem(leader))
                return;

            ClanData clan = GetClan(leader.userID);
            if (clan == null)
            {
                SendReply(leader, "Для захвата нужен клан.");
                return;
            }
            if (!IsLeader(clan, leader.userID))
            {
                SendReply(leader, "Начать захват может только глава клана.");
                return;
            }

            RuntimeTerritory territory = FindTerritoryNear(leader.transform.position, _config.Capture.StartDistance);
            if (territory == null)
            {
                SendReply(leader, "Вы должны находиться непосредственно в зоне точки захвата Аэродрома/Космодрома.");
                return;
            }
            if (territory.Monument == null)
            {
                SendReply(leader, "Этот РТ не найден на текущей карте.");
                return;
            }
            if (!territory.Config.Calibrated)
            {
                SendReply(leader, "Точка генератора для этого РТ ещё не настроена администратором.");
                return;
            }
            if (!string.IsNullOrEmpty(territory.State.CapturingTag))
            {
                SendReply(leader, "Этот РТ уже захватывают.");
                return;
            }
            int activeForClan = _runtimeTerritories.Values.Count(t => string.Equals(t.State.CapturingTag, clan.Tag, StringComparison.OrdinalIgnoreCase));
            if (activeForClan >= _config.Capture.MaxSimultaneousCapturesPerClan)
            {
                SendReply(leader, "Ваш клан уже ведёт максимально допустимое количество одновременных захватов.");
                return;
            }
            if (string.Equals(territory.State.OwnerTag, clan.Tag, StringComparison.OrdinalIgnoreCase))
            {
                SendReply(leader, "Этот РТ уже принадлежит вашему клану.");
                return;
            }
            if (territory.State.CooldownUntilUnix > UnixNow())
            {
                SendReply(leader, "РТ на кулдауне ещё " + FormatDuration(territory.State.CooldownUntilUnix - UnixNow()) + ".");
                return;
            }

            int inside = CountClanMembersInside(clan.Tag, territory);
            bool adminSoloTest = !IsPublicSystemEnabled && HasAdminPermission(leader) && _config.Access.AllowAdministratorTesting;
            int requiredToStart = adminSoloTest ? 1 : _config.Capture.MinimumMembersToStart;
            if (inside < requiredToStart)
            {
                SendReply(leader, string.Format("Для старта нужно минимум {0} участника(ов) клана внутри территории. Сейчас: {1}.", requiredToStart, inside));
                return;
            }

            if (!SpawnCaptureGenerator(territory))
            {
                SendReply(leader, "Не удалось создать генератор. Проверьте точку РТ и консоль.");
                return;
            }

            territory.State.CapturingTag = clan.Tag;
            territory.State.ProgressSeconds = 0f;
            territory.State.CaptureStartedUnix = UnixNow();
            territory.State.TakeoverLockedUntilUnix = 0;
            territory.State.Status = "waiting_fuel";
            RecordCaptureStarted(clan, territory);
            SaveData();

            AnnounceSystem(string.Format("<color=#d6a84b>[КЛАНЫ]</color> Клан <color={0}>[{1}]</color> начал захват <color=#f0c674>{2}</color>. Генератор установлен на точке захвата — атакующим нужно заправить его ТНК и удержать территорию.", _config.Chat.TagColor, clan.Tag, territory.Config.DisplayName));
            UpdateCaptureHudForAll();
        }

        private bool CanTakeoverCapture(ClanData clan, ulong playerId)
        {
            return clan != null && (IsLeader(clan, playerId) || IsDeputy(clan, playerId));
        }

        private void TryTakeoverNearestCapture(BasePlayer player)
        {
            if (player == null || !CanAccessSystem(player))
                return;

            RuntimeTerritory territory = FindTerritoryNear(player.transform.position, _config.Capture.StartDistance);
            if (territory == null || string.IsNullOrEmpty(territory.State.CapturingTag))
            {
                SendReply(player, "Рядом нет активного захвата, который можно перехватить.");
                return;
            }

            TryTakeoverCapture(player, territory.Config.Id);
        }

        private void TryTakeoverCapture(BasePlayer player, string territoryId)
        {
            if (player == null || !CanAccessSystem(player) || string.IsNullOrEmpty(territoryId))
                return;

            if (!_config.Capture.AllowCaptureTakeover)
            {
                SendReply(player, "Перехват активного захвата отключён в конфиге.");
                return;
            }

            RuntimeTerritory territory;
            if (!_runtimeTerritories.TryGetValue(territoryId, out territory) || territory == null)
            {
                SendReply(player, "Точка захвата не найдена.");
                return;
            }

            ClanData challenger = GetClan(player.userID);
            if (challenger == null)
            {
                SendReply(player, "Для перехвата нужен клан.");
                return;
            }

            if (!CanTakeoverCapture(challenger, player.userID))
            {
                SendReply(player, "Перехватить захват может только глава или заместитель главы клана.");
                return;
            }

            if (string.IsNullOrEmpty(territory.State.CapturingTag))
            {
                SendReply(player, "Сейчас эту территорию никто не захватывает.");
                return;
            }

            if (string.Equals(territory.State.CapturingTag, challenger.Tag, StringComparison.OrdinalIgnoreCase))
            {
                SendReply(player, "Этот захват уже ведёт ваш клан.");
                return;
            }

            if (territory.Monument == null || !territory.Config.Calibrated)
            {
                SendReply(player, "Точка захвата сейчас недоступна.");
                return;
            }

            float distance = Vector3.Distance(player.transform.position, territory.CapturePoint);
            if (distance > _config.Capture.StartDistance)
            {
                SendReply(player, "Подойдите к самой точке захвата.");
                return;
            }

            long now = UnixNow();
            if (territory.State.TakeoverLockedUntilUnix > now)
            {
                SendReply(player, "Точку только что перехватили. Повторный перехват будет доступен через " + FormatDuration(territory.State.TakeoverLockedUntilUnix - now) + ".");
                return;
            }

            if (territory.Generator == null || territory.Generator.IsDestroyed)
            {
                SendReply(player, "Ивентовый генератор отсутствует — этот захват нельзя перехватить.");
                return;
            }

            if (_config.Capture.TakeoverRequiresEmptyFuel && EventGeneratorHasFuel(territory.Generator))
            {
                SendReply(player, "Сначала полностью выньте ТНК из ивентового генератора. Пока в нём есть топливо, перехват невозможен.");
                return;
            }

            int inside = CountClanMembersInside(challenger.Tag, territory);
            if (inside < 1)
            {
                SendReply(player, "Для перехвата хотя бы один участник вашего клана должен находиться на территории.");
                return;
            }

            string previousCapturingTag = territory.State.CapturingTag;
            ClanData previousClan = GetClanByTag(previousCapturingTag);

            territory.State.CapturingTag = challenger.Tag;
            territory.State.ProgressSeconds = 0f;
            territory.State.CaptureStartedUnix = now;
            territory.State.Status = "waiting_fuel";
            territory.State.TakeoverLockedUntilUnix = now + _config.Capture.TakeoverProtectionSeconds;
            SetGeneratorVisualState(territory, false);

            RecordCaptureTakeover(challenger, previousCapturingTag, territory);
            SaveData();
            UpdateCaptureHudForAll();

            if (previousClan != null)
                BroadcastToClan(previousClan, string.Format("Ваш захват {0} перехватил клан [{1}]. Прогресс сброшен.", territory.Config.DisplayName, challenger.Tag));
            BroadcastToClan(challenger, string.Format("Вы перехватили захват {0} у клана [{1}]. Положите своё ТНК в генератор — прогресс начнётся с нуля.", territory.Config.DisplayName, previousCapturingTag));

            AnnounceSystem(string.Format("<color=#d6a84b>[КЛАНЫ]</color> Клан <color={0}>[{1}]</color> перехватил захват <color=#f0c674>{2}</color> у клана <color={0}>[{3}]</color>. Старое ТНК было убрано, прогресс сброшен. Новому клану нужно заправить генератор своим ТНК.", _config.Chat.TagColor, challenger.Tag, territory.Config.DisplayName, previousCapturingTag));
        }

        private bool SpawnCaptureGenerator(RuntimeTerritory territory)
        {
            KillCaptureGenerator(territory, false);

            if (string.IsNullOrEmpty(_generatorPrefabPath)) ResolveGeneratorPrefabPath();
            if (string.IsNullOrEmpty(_generatorPrefabPath))
            {
                PrintError("Не найден prefab предмета electric.fuelgenerator.small.");
                return false;
            }
            BaseEntity entity = GameManager.server.CreateEntity(_generatorPrefabPath, territory.CapturePoint, Quaternion.identity, true);
            FuelGenerator generator = entity as FuelGenerator;
            if (generator == null)
            {
                if (entity != null) entity.Kill();
                PrintError("Не удалось создать FuelGenerator из prefab: " + _generatorPrefabPath);
                return false;
            }

            generator.OwnerID = 0UL;
            generator.Spawn();

            BaseCombatEntity combat = generator as BaseCombatEntity;
            if (combat != null)
            {
                combat._maxHealth = _config.Capture.GeneratorHealth;
                combat.health = _config.Capture.GeneratorHealth;
            }

            territory.Generator = generator;
            territory.State.GeneratorNetId = generator.net.ID.Value;
            _captureGeneratorNetIds.Add(territory.State.GeneratorNetId);
            SetGeneratorVisualState(territory, false);

            return true;
        }

        private void CaptureTick()
        {
            if (!_serverReady)
                return;

            long now = UnixNow();

            foreach (RuntimeTerritory territory in _runtimeTerritories.Values)
            {
                ProcessHoldRewards(territory, now);

                if (string.IsNullOrEmpty(territory.State.CapturingTag))
                    continue;

                ClanData capturingClan = GetClanByTag(territory.State.CapturingTag);
                if (capturingClan == null)
                {
                    CancelCapture(territory, "Захват отменён: клан больше не существует.", false);
                    continue;
                }

                if (territory.Generator == null || territory.Generator.IsDestroyed)
                {
                    if (_config.Capture.CancelOnGeneratorDestroyed)
                    {
                        CancelCapture(territory, "Генератор уничтожен — захват сорван.", true);
                        continue;
                    }

                    // Захват не отменяется — ставим новый пустой генератор на ту же точку.
                    if (!SpawnCaptureGenerator(territory))
                        continue;
                    BroadcastToClan(capturingClan, string.Format("Генератор на {0} был уничтожен и поставлен заново. Заправьте его ТНК.", territory.Config.DisplayName));
                }

                int friendly = CountClanMembersInside(capturingClan.Tag, territory);
                HashSet<string> enemyClans = GetEnemyClansInside(capturingClan.Tag, territory);

                if (enemyClans.Count > 0)
                {
                    territory.State.Status = EventGeneratorHasFuel(territory.Generator) ? "contested" : "takeover_ready";
                    RefreshEventGeneratorPowerState(territory);
                    continue;
                }

                if (friendly < _config.Capture.MinimumMembersToProgress)
                {
                    territory.State.Status = "no_defenders";
                    territory.State.ProgressSeconds = Mathf.Max(0f, territory.State.ProgressSeconds - _config.Capture.ProgressLossWithoutDefendersSeconds);
                    RefreshEventGeneratorPowerState(territory);
                    continue;
                }

                if (!ConsumeCaptureFuel(territory, _config.Capture.FuelPerTick))
                {
                    territory.State.Status = "waiting_fuel";
                    RefreshEventGeneratorPowerState(territory);
                    continue;
                }

                territory.State.Status = "capturing";
                territory.State.ProgressSeconds += _config.Capture.TickSeconds;
                RefreshEventGeneratorPowerState(territory);

                if (territory.State.ProgressSeconds >= _config.Capture.CaptureDurationSeconds)
                    CompleteCapture(territory, capturingClan);
            }

            UpdateCaptureHudForAll();
            RefreshLiveViews();
        }

        private void ProcessHoldRewards(RuntimeTerritory territory, long now)
        {
            if (!IsPublicSystemEnabled || string.IsNullOrEmpty(territory.State.OwnerTag))
                return;
            if (territory.State.NextHoldRewardUnix <= 0)
            {
                territory.State.NextHoldRewardUnix = now + _config.Score.HoldRewardIntervalSeconds;
                return;
            }
            if (now < territory.State.NextHoldRewardUnix)
                return;

            ClanData owner = GetClanByTag(territory.State.OwnerTag);
            if (owner == null)
            {
                territory.State.OwnerTag = string.Empty;
                return;
            }

            if (_config.Score.PauseHoldWhileEnemyCapturing &&
                !string.IsNullOrEmpty(territory.State.CapturingTag) &&
                !string.Equals(territory.State.CapturingTag, owner.Tag, StringComparison.OrdinalIgnoreCase))
            {
                territory.State.NextHoldRewardUnix = now + _config.Score.HoldRewardIntervalSeconds;
                return;
            }

            int requiredInside = _config.Score.MinimumMembersInsideForHoldReward;
            if (requiredInside > 0 && CountClanMembersInside(owner.Tag, territory) < requiredInside)
            {
                territory.State.NextHoldRewardUnix = now + _config.Score.HoldRewardIntervalSeconds;
                return;
            }

            int awarded = AddScore(owner, _config.Score.HoldPoints, ScoreSource.Hold);
            if (awarded > 0 && _data.Statistics != null)
                _data.Statistics.HoldPoints += awarded;
            if (awarded > 0)
                territory.State.HoldTicksAwarded++;
            territory.State.NextHoldRewardUnix = now + _config.Score.HoldRewardIntervalSeconds;
            if (awarded > 0)
                BroadcastToClan(owner, string.Format("Удержание {0}: +{1} очк. Клан продолжает получать очки, пока участники находятся на территории.", territory.Config.DisplayName, awarded));
            SaveData();
        }

        private FuelGenerator GetEventGeneratorFromContainer(ItemContainer container)
        {
            if (container == null || container.entityOwner == null)
                return null;

            FuelGenerator generator = container.entityOwner as FuelGenerator;
            if (generator == null)
            {
                ItemBasedFlowRestrictor box = container.entityOwner as ItemBasedFlowRestrictor;
                if (box != null)
                    generator = box.GetParentEntity() as FuelGenerator;
            }

            if (generator == null || generator.net == null)
                return null;
            return _captureGeneratorNetIds.Contains(generator.net.ID.Value) ? generator : null;
        }

        private List<ItemContainer> GetEventGeneratorFuelContainers(FuelGenerator generator)
        {
            List<ItemContainer> result = new List<ItemContainer>();
            if (generator == null)
                return result;

            if (generator.inventory != null)
                result.Add(generator.inventory);

            if (generator.children != null)
            {
                foreach (ItemBasedFlowRestrictor box in generator.children.OfType<ItemBasedFlowRestrictor>())
                {
                    if (box != null && box.inventory != null && !result.Contains(box.inventory))
                        result.Add(box.inventory);
                }
            }

            return result;
        }

        private bool EventGeneratorHasFuel(FuelGenerator generator)
        {
            foreach (ItemContainer container in GetEventGeneratorFuelContainers(generator))
            {
                if (container == null || container.itemList == null)
                    continue;
                foreach (Item item in container.itemList)
                {
                    if (item != null && item.amount > 0 && item.info != null && string.Equals(item.info.shortname, LowGradeShortname, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        private void RefreshEventGeneratorPowerState(RuntimeTerritory territory)
        {
            if (territory == null || territory.Generator == null || territory.Generator.IsDestroyed)
                return;
            SetGeneratorVisualState(territory, EventGeneratorHasFuel(territory.Generator));
        }

        private bool ConsumeCaptureFuel(RuntimeTerritory territory, int amount)
        {
            if (territory == null || territory.Generator == null || territory.Generator.IsDestroyed)
                return false;

            List<ItemContainer> containers = GetEventGeneratorFuelContainers(territory.Generator);
            int available = 0;
            foreach (ItemContainer container in containers)
            {
                if (container == null || container.itemList == null)
                    continue;
                foreach (Item item in container.itemList)
                {
                    if (item != null && item.info != null && string.Equals(item.info.shortname, LowGradeShortname, StringComparison.OrdinalIgnoreCase))
                        available += item.amount;
                }
            }

            if (available < amount)
                return false;

            int left = amount;
            _manualCaptureFuelConsume = true;
            try
            {
                foreach (ItemContainer container in containers)
                {
                    if (container == null || container.itemList == null)
                        continue;
                    foreach (Item item in container.itemList.ToList())
                    {
                        if (left <= 0)
                            break;
                        if (item == null || item.info == null || !string.Equals(item.info.shortname, LowGradeShortname, StringComparison.OrdinalIgnoreCase))
                            continue;

                        int take = Math.Min(left, item.amount);
                        item.UseItem(take);
                        left -= take;
                    }
                    container.MarkDirty();
                    if (left <= 0)
                        break;
                }
            }
            finally
            {
                _manualCaptureFuelConsume = false;
            }

            RefreshEventGeneratorPowerState(territory);
            return left <= 0;
        }

        private void CompleteCapture(RuntimeTerritory territory, ClanData clan)
        {
            string previousOwner = territory.State.OwnerTag;
            int requestedPoints = _config.Score.CapturePoints;
            if (!string.IsNullOrEmpty(previousOwner) && !string.Equals(previousOwner, clan.Tag, StringComparison.OrdinalIgnoreCase))
                requestedPoints += _config.Score.TakeoverBonusPoints;
            int awarded = AddScore(clan, requestedPoints, ScoreSource.Capture);
            if (IsPublicSystemEnabled)
                clan.Captures++;
            RecordCaptureSuccess(clan, territory, awarded, previousOwner);

            territory.State.OwnerTag = clan.Tag;
            territory.State.CapturingTag = string.Empty;
            territory.State.ProgressSeconds = 0f;
            territory.State.LastCapturedUnix = UnixNow();
            territory.State.CooldownUntilUnix = UnixNow() + _config.Capture.TerritoryCooldownSeconds;
            territory.State.HoldTicksAwarded = 0;
            territory.State.NextHoldRewardUnix = UnixNow() + _config.Score.HoldRewardIntervalSeconds;
            territory.State.Status = "owned";
            KillCaptureGenerator(territory, false);
            SaveData();
            BroadcastToClan(clan, string.Format("Территория {0} ваша! Пока участники на ней, клан получает +{1} очк. каждые {2}.", territory.Config.DisplayName, _config.Score.HoldPoints, FormatMinutes(_config.Score.HoldRewardIntervalSeconds)));

            string takeover = string.IsNullOrEmpty(previousOwner) ? string.Empty : string.Format(" Территория отбита у [{0}].", previousOwner);
            AnnounceSystem(string.Format("<color=#d6a84b>[КЛАНЫ]</color> Захват завершён <color=#8fd477><b>УСПЕШНО</b></color>: клан <color={0}>[{1}]</color> захватил <color=#f0c674>{2}</color>{3}{4}", _config.Chat.TagColor, clan.Tag, territory.Config.DisplayName, IsPublicSystemEnabled ? string.Format(" +<color=#9ad06d>{0}</color> очк.", awarded) : " Тестовый режим: очки не начислены.", takeover));
        }

        private void CancelCapture(RuntimeTerritory territory, string reason, bool applyShortCooldown)
        {
            string tag = territory.State.CapturingTag;
            RecordCaptureFailure(tag, territory, reason);
            territory.State.CapturingTag = string.Empty;
            territory.State.ProgressSeconds = 0f;
            territory.State.CaptureStartedUnix = 0;
            territory.State.TakeoverLockedUntilUnix = 0;
            territory.State.Status = string.IsNullOrEmpty(territory.State.OwnerTag) ? "idle" : "owned";
            if (applyShortCooldown)
                territory.State.CooldownUntilUnix = Math.Max(territory.State.CooldownUntilUnix, UnixNow() + _config.Capture.FailedCaptureCooldownSeconds);

            KillCaptureGenerator(territory, false);
            SaveData();
            UpdateCaptureHudForAll();
            AnnounceSystem(string.Format("<color=#d6a84b>[КЛАНЫ]</color> Захват завершён <color=#e07063><b>НЕУСПЕШНО</b></color>: клан <color={0}>[{1}]</color> не смог захватить <color=#f0c674>{2}</color>. {3}", _config.Chat.TagColor, tag, territory.Config.DisplayName, reason));
        }

        private void CancelOwnCapture(BasePlayer leader)
        {
            if (leader == null || !CanAccessSystem(leader))
                return;

            ClanData clan = GetClan(leader.userID);
            if (clan == null || !IsLeader(clan, leader.userID))
            {
                SendReply(leader, "Отказаться от захвата может только глава клана.");
                return;
            }

            RuntimeTerritory territory = _runtimeTerritories.Values.FirstOrDefault(t => string.Equals(t.State.CapturingTag, clan.Tag, StringComparison.OrdinalIgnoreCase));
            if (territory == null)
            {
                SendReply(leader, "Ваш клан сейчас ничего не захватывает.");
                return;
            }

            string tag = clan.Tag;
            RecordCaptureManualCancel(clan, territory);
            territory.State.CapturingTag = string.Empty;
            territory.State.ProgressSeconds = 0f;
            territory.State.CaptureStartedUnix = 0;
            territory.State.TakeoverLockedUntilUnix = 0;
            territory.State.Status = string.IsNullOrEmpty(territory.State.OwnerTag) ? "idle" : "owned";
            KillCaptureGenerator(territory, false);
            SaveData();
            UpdateCaptureHudForAll();

            AnnounceSystem(string.Format("<color=#d6a84b>[КЛАНЫ]</color> Захват завершён <color=#e07063><b>НЕУСПЕШНО</b></color>: глава клана <color={0}>[{1}]</color> отказался от захвата <color=#f0c674>{2}</color>. Генератор и оставшееся в нём топливо удалены, очки не начислены.", _config.Chat.TagColor, tag, territory.Config.DisplayName));
        }

        private void EnsureStatisticsStorage()
        {
            if (_data.Statistics == null)
                _data.Statistics = new PluginStatistics();
            if (_data.StatisticsEvents == null)
                _data.StatisticsEvents = new List<PluginEventEntry>();
            if (_data.HourlyStatistics == null)
                _data.HourlyStatistics = new List<HourlyStatisticsBucket>();
        }

        private HourlyStatisticsBucket GetCurrentStatisticsBucket()
        {
            EnsureStatisticsStorage();
            long hour = (UnixNow() / 3600L) * 3600L;
            HourlyStatisticsBucket bucket = _data.HourlyStatistics.FirstOrDefault(x => x != null && x.HourUnix == hour);
            if (bucket == null)
            {
                bucket = new HourlyStatisticsBucket { HourUnix = hour };
                _data.HourlyStatistics.Add(bucket);
            }

            long oldest = hour - 72L * 3600L;
            _data.HourlyStatistics.RemoveAll(x => x == null || x.HourUnix < oldest);
            return bucket;
        }

        private void AddStatisticsEvent(string type, string clanTag, RuntimeTerritory territory, string text)
        {
            EnsureStatisticsStorage();
            _data.Statistics.LastActivityUnix = UnixNow();
            _data.StatisticsEvents.Add(new PluginEventEntry
            {
                Unix = UnixNow(),
                Type = type ?? string.Empty,
                ClanTag = clanTag ?? string.Empty,
                TerritoryId = territory != null && territory.Config != null ? territory.Config.Id : string.Empty,
                Text = text ?? string.Empty
            });

            int limit = _config != null && _config.AdminUi != null ? _config.AdminUi.StatisticsEventLogLimit : 200;
            if (_data.StatisticsEvents.Count > limit)
                _data.StatisticsEvents.RemoveRange(0, _data.StatisticsEvents.Count - limit);
        }

        private void RecordCaptureStarted(ClanData clan, RuntimeTerritory territory)
        {
            EnsureStatisticsStorage();
            _data.Statistics.CaptureAttempts++;
            GetCurrentStatisticsBucket().CaptureStarts++;
            AddStatisticsEvent("START", clan == null ? string.Empty : clan.Tag, territory,
                string.Format("[{0}] начал захват {1}.", clan == null ? "?" : clan.Tag, territory.Config.DisplayName));
        }

        private void RecordCaptureSuccess(ClanData clan, RuntimeTerritory territory, int awarded, string previousOwner)
        {
            EnsureStatisticsStorage();
            _data.Statistics.CaptureSuccesses++;
            if (awarded > 0)
                _data.Statistics.CapturePoints += awarded;
            GetCurrentStatisticsBucket().CaptureSuccesses++;
            AddStatisticsEvent("SUCCESS", clan == null ? string.Empty : clan.Tag, territory,
                string.Format("[{0}] успешно захватил {1}{2}.", clan == null ? "?" : clan.Tag, territory.Config.DisplayName,
                    string.IsNullOrEmpty(previousOwner) ? string.Empty : " у [" + previousOwner + "]"));
        }

        private void RecordCaptureFailure(string clanTag, RuntimeTerritory territory, string reason)
        {
            EnsureStatisticsStorage();
            _data.Statistics.CaptureFailures++;
            if (!string.IsNullOrEmpty(reason) && reason.IndexOf("Генератор уничтожен", StringComparison.OrdinalIgnoreCase) >= 0)
                _data.Statistics.GeneratorDestroyedFailures++;
            GetCurrentStatisticsBucket().CaptureFailures++;
            AddStatisticsEvent("FAIL", clanTag, territory,
                string.Format("[{0}] не смог захватить {1}: {2}", string.IsNullOrEmpty(clanTag) ? "?" : clanTag, territory.Config.DisplayName, reason));
        }

        private void RecordCaptureManualCancel(ClanData clan, RuntimeTerritory territory)
        {
            EnsureStatisticsStorage();
            _data.Statistics.CaptureManualCancels++;
            GetCurrentStatisticsBucket().CaptureManualCancels++;
            AddStatisticsEvent("CANCEL", clan == null ? string.Empty : clan.Tag, territory,
                string.Format("[{0}] добровольно отказался от захвата {1}.", clan == null ? "?" : clan.Tag, territory.Config.DisplayName));
        }

        private void RecordCaptureTakeover(ClanData challenger, string previousCapturingTag, RuntimeTerritory territory)
        {
            EnsureStatisticsStorage();
            _data.Statistics.CaptureTakeovers++;
            _data.Statistics.CaptureAttempts++;
            HourlyStatisticsBucket bucket = GetCurrentStatisticsBucket();
            bucket.CaptureTakeovers++;
            bucket.CaptureStarts++;
            AddStatisticsEvent("TAKEOVER", challenger == null ? string.Empty : challenger.Tag, territory,
                string.Format("[{0}] перехватил активный захват {1} у [{2}].", challenger == null ? "?" : challenger.Tag, territory.Config.DisplayName, string.IsNullOrEmpty(previousCapturingTag) ? "?" : previousCapturingTag));
        }

        private void RecordPvpStatistic(ClanData attackerClan, BasePlayer attacker, BasePlayer victim, int awarded)
        {
            EnsureStatisticsStorage();
            _data.Statistics.PvpKills++;
            if (awarded > 0)
                _data.Statistics.PvpPoints += awarded;
            GetCurrentStatisticsBucket().PvpKills++;
            AddStatisticsEvent("PVP", attackerClan == null ? string.Empty : attackerClan.Tag, null,
                string.Format("[{0}] {1} убил участника другого клана {2}. +{3} очк.",
                    attackerClan == null ? "?" : attackerClan.Tag,
                    attacker == null ? "?" : attacker.displayName,
                    victim == null ? "?" : victim.displayName,
                    awarded));
        }

        private void ResetPluginStatistics()
        {
            _data.Statistics = new PluginStatistics();
            _data.StatisticsEvents = new List<PluginEventEntry>();
            _data.HourlyStatistics = new List<HourlyStatisticsBucket>();
            SaveData();
        }

        private void CancelCapturesByClan(string tag, string reason)
        {
            foreach (RuntimeTerritory territory in _runtimeTerritories.Values)
            {
                if (string.Equals(territory.State.CapturingTag, tag, StringComparison.OrdinalIgnoreCase))
                    CancelCapture(territory, reason, false);
            }
        }

        private void ClearTerritoryOwnership(string tag)
        {
            foreach (TerritoryState state in _data.Territories.Values)
            {
                if (string.Equals(state.OwnerTag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    state.OwnerTag = string.Empty;
                    state.HoldTicksAwarded = 0;
                    state.NextHoldRewardUnix = 0;
                }
            }
        }

        private void KillCaptureGenerator(RuntimeTerritory territory, bool dropFuel)
        {
            if (territory == null)
                return;

            FuelGenerator generator = territory.Generator;
            ulong generatorNetId = generator != null && generator.net != null ? generator.net.ID.Value : territory.State.GeneratorNetId;
            if (generatorNetId != 0UL)
                _captureGeneratorNetIds.Remove(generatorNetId);
            territory.Generator = null;
            territory.State.GeneratorNetId = 0UL;

            if (generator != null && !generator.IsDestroyed)
            {
                if (!dropFuel)
                {
                    if (generator.inventory != null)
                        generator.inventory.Clear();
                    if (generator.children != null)
                    {
                        ItemBasedFlowRestrictor box = generator.children.OfType<ItemBasedFlowRestrictor>().FirstOrDefault();
                        if (box != null && box.inventory != null)
                            box.inventory.Clear();
                    }
                }
                generator.Kill();
            }
        }

        private void SetGeneratorVisualState(RuntimeTerritory territory, bool on)
        {
            if (territory == null || territory.Generator == null || territory.Generator.IsDestroyed || territory.Generator.net == null)
                return;

            // Трогаем только конкретный генератор активного захвата, зарегистрированный плагином.
            if (!_captureGeneratorNetIds.Contains(territory.Generator.net.ID.Value))
                return;

            if (territory.Generator.HasFlag(BaseEntity.Flags.On) != on)
            {
                using (var flagsScope = territory.Generator.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate_Flags))
                {
                    flagsScope.Set(BaseEntity.Flags.On, on);
                }
            }
            territory.Generator.SendNetworkUpdateImmediate();
        }

        private int CountClanMembersInside(string clanTag, RuntimeTerritory territory)
        {
            ClanData clan = GetClanByTag(clanTag);
            if (clan == null) return 0;

            int count = 0;
            foreach (ulong id in clan.Members)
            {
                BasePlayer player = BasePlayer.FindByID(id);
                if (player != null && player.IsConnected && CanAccessSystem(player) && !player.IsDead() && Vector3.Distance(player.transform.position, territory.CapturePoint) <= territory.Config.Radius)
                    count++;
            }
            return count;
        }

        private HashSet<string> GetEnemyClansInside(string capturingTag, RuntimeTerritory territory)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || !CanAccessSystem(player) || player.IsDead()) continue;
                if (Vector3.Distance(player.transform.position, territory.CapturePoint) > territory.Config.Radius) continue;

                ClanData clan = GetClan(player.userID);
                if (clan != null && !string.Equals(clan.Tag, capturingTag, StringComparison.OrdinalIgnoreCase))
                    result.Add(clan.Tag);
            }
            return result;
        }

        private RuntimeTerritory FindTerritoryNear(Vector3 position, float maxDistance)
        {
            return _runtimeTerritories.Values
                .Where(t => t.Monument != null)
                .OrderBy(t => Vector3.Distance(position, t.CapturePoint))
                .FirstOrDefault(t => Vector3.Distance(position, t.CapturePoint) <= maxDistance);
        }

        private RuntimeTerritory FindRuntimeByGenerator(ulong netId)
        {
            if (netId == 0UL) return null;
            return _runtimeTerritories.Values.FirstOrDefault(t => t.State.GeneratorNetId == netId || (t.Generator != null && t.Generator.net != null && t.Generator.net.ID.Value == netId));
        }

        #endregion

        #region Scoring / Season

        private enum ScoreSource
        {
            Kill,
            Capture,
            Hold,
            Admin
        }

        private int AddScore(ClanData clan, int requested, ScoreSource source)
        {
            if (!IsPublicSystemEnabled && source != ScoreSource.Admin)
                return 0;
            if (clan == null || requested <= 0)
                return 0;

            clan.Score += requested;
            clan.LastScoreUnix = UnixNow();
            switch (source)
            {
                case ScoreSource.Kill: clan.KillScore += requested; break;
                case ScoreSource.Capture: clan.CaptureScore += requested; break;
                case ScoreSource.Hold: clan.HoldScore += requested; break;
                case ScoreSource.Admin: clan.AdminScore += requested; break;
            }
            return requested;
        }

        // Корректировка очков администратором: поддерживает минус и точное значение,
        // учитывается отдельной строкой «Администратор» и пишется в журнал.
        private int AdminAdjustScore(BasePlayer admin, ClanData clan, int value, bool absolute)
        {
            if (clan == null)
                return 0;

            value = Mathf.Clamp(value, -1000000, 1000000);
            int before = clan.Score;
            int after = absolute ? Math.Max(0, value) : Math.Max(0, before + value);
            int actual = after - before;
            if (actual == 0)
            {
                if (admin != null) SendReply(admin, string.Format("Очки [{0}] не изменились: {1}.", clan.Tag, before));
                return 0;
            }

            clan.Score = after;
            clan.AdminScore += actual;
            clan.LastScoreUnix = UnixNow();
            AddStatisticsEvent("ADMIN", clan.Tag, null, string.Format("{0} изменил очки [{1}]: {2}{3} (было {4}, стало {5}).",
                admin == null ? "Консоль" : admin.displayName, clan.Tag, actual > 0 ? "+" : string.Empty, actual, before, after));
            SaveData();
            if (admin != null)
                SendReply(admin, string.Format("Очки [{0}]: {1} → <b>{2}</b> ({3}{4}).", clan.Tag, before, after, actual > 0 ? "+" : string.Empty, actual));
            return actual;
        }

        private void ResetClanStats(ClanData clan)
        {
            if (clan == null)
                return;
            clan.Score = clan.DailyScore = clan.DailyKillScore = clan.Kills = clan.Captures = clan.DailyCaptures = 0;
            clan.KillScore = clan.CaptureScore = clan.HoldScore = clan.AdminScore = 0;
            clan.LastScoreUnix = 0;
            if (clan.PlayerKills == null)
                clan.PlayerKills = new Dictionary<ulong, int>();
            foreach (ulong memberId in clan.Members)
                clan.PlayerKills[memberId] = 0;
            SaveData();
        }

        // Старые суточные поля сохраняются только для чтения прежних data-файлов и на рейтинг не влияют.

        private void ResetSeason(bool fromWipe, bool announce)
        {
            ClanData winner = GetLeaderboard().FirstOrDefault(c => c.Score > 0);
            if (winner != null)
            {
                _data.LastWinnerTag = winner.Tag;
                _data.LastWinnerScore = winner.Score;
            }

            foreach (ClanData clan in _data.Clans.Values)
            {
                clan.Score = 0;
                clan.KillScore = 0;
                clan.CaptureScore = 0;
                clan.HoldScore = 0;
                clan.AdminScore = 0;
                clan.DailyScore = 0;
                clan.DailyKillScore = 0;
                clan.DailyCaptures = 0;
                clan.Kills = 0;
                clan.Captures = 0;
                clan.LastScoreUnix = 0;
                if (clan.PlayerKills == null)
                    clan.PlayerKills = new Dictionary<ulong, int>();
                foreach (ulong memberId in clan.Members)
                    clan.PlayerKills[memberId] = 0;
                if (fromWipe)
                {
                    clan.NameChangedThisWipe = false;
                    clan.TagChangedThisWipe = false;
                }
            }

            foreach (RuntimeTerritory runtime in _runtimeTerritories.Values)
            {
                KillCaptureGenerator(runtime, false);
                ResetTerritoryState(runtime.State);
            }

            foreach (TerritoryState state in _data.Territories.Values)
                ResetTerritoryState(state);

            _data.KillRewardCooldowns.Clear();
            _data.DailyKey = "ОТКЛЮЧЕНО";
            _data.SeasonStartedUnix = UnixNow();
            SaveData();
            UpdateCaptureHudForAll();

            if (announce)
            {
                string lastWinner = string.IsNullOrEmpty(_data.LastWinnerTag)
                    ? string.Empty
                    : string.Format(" Победитель прошлого сезона: <color={0}>[{1}]</color> — {2} очк.", _config.Chat.TagColor, _data.LastWinnerTag, _data.LastWinnerScore);
                AnnounceSystem("<color=#d6a84b>[КЛАНЫ]</color> " + (fromWipe ? "Новый вайп — клановый рейтинг обнулён." : "Клановый сезон сброшен администратором.") + lastWinner);
            }
        }

        private void ResetClanEditLimitsForNewWipe()
        {
            foreach (ClanData clan in _data.Clans.Values)
            {
                clan.NameChangedThisWipe = false;
                clan.TagChangedThisWipe = false;
            }
        }

        private void ResetTerritoryState(TerritoryState state)
        {
            if (state == null) return;
            state.OwnerTag = string.Empty;
            state.CapturingTag = string.Empty;
            state.ProgressSeconds = 0f;
            state.CaptureStartedUnix = 0;
            state.CooldownUntilUnix = 0;
            state.LastCapturedUnix = 0;
            state.HoldTicksAwarded = 0;
            state.NextHoldRewardUnix = 0;
            state.GeneratorNetId = 0UL;
            state.Status = "idle";
        }

        private List<ClanData> GetLeaderboard()
        {
            return _data.Clans.Values
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.LastScoreUnix <= 0 ? long.MaxValue : c.LastScoreUnix)
                .ThenByDescending(c => c.Captures)
                .ThenByDescending(c => c.Kills)
                .ThenBy(c => c.ClanId)
                .ToList();
        }

        private void PrintLeaderboard(BasePlayer player)
        {
            List<ClanData> top = GetLeaderboard().Take(10).ToList();
            if (top.Count == 0)
            {
                SendReply(player, "Кланов пока нет.");
                return;
            }

            SendReply(player, "<size=16><color=#d6a84b>ТОП КЛАНОВ ВАЙПА</color></size>");
            for (int i = 0; i < top.Count; i++)
            {
                ClanData clan = top[i];
                SendReply(player, string.Format("{0}. <color={1}>[{2}]</color> — <b>{3}</b> очк. | захваты {4} | PvP {5}", i + 1, _config.Chat.TagColor, clan.Tag, clan.Score, clan.Captures, clan.Kills));
            }
        }

        #endregion

        #region GUI

        // Стиль ванильного Rust
        // Та же палитра, что в колесе удачи: тёмные тёплые панели с текстурой,
        // оранжевый акцент, зелёные/красные игровые кнопки, иконки предметов.

        private const string FontBold = "robotocondensed-bold.ttf";
        private const string FontReg = "robotocondensed-regular.ttf";
        private const string SpriteTile = "assets/content/ui/ui.background.tile.psd";
        private const string MatBlur = "assets/content/ui/uibackgroundblur-ingamemenu.mat";
        private const string IconClose = "assets/icons/close.png";
        private const string SpriteCircle = "assets/icons/circle_closed.png";

        private const string ColWindow = "0.106 0.106 0.098 0.98";
        private const string ColHeader = "0.180 0.172 0.157 1";
        private const string ColPanel = "0.141 0.137 0.125 0.97";
        private const string ColPanelDark = "0.090 0.090 0.082 0.97";
        private const string ColTrack = "0.075 0.075 0.068 1";
        private const string ColSlotSolid = "0.200 0.196 0.180 1";
        private const string ColRowA = "0.165 0.161 0.149 0.95";
        private const string ColRowB = "0.133 0.129 0.120 0.95";
        private const string ColBorder = "0.290 0.282 0.255 0.9";
        private const string ColText = "0.910 0.871 0.827 1";
        private const string ColTextDim = "0.620 0.592 0.553 1";
        private const string ColTextMute = "0.450 0.430 0.400 1";
        private const string ColAccent = "0.804 0.329 0.196 1";
        private const string ColAccentSoft = "0.804 0.329 0.196 0.18";
        private const string ColAccentTxt = "0.960 0.600 0.450 1";
        private const string ColGreenBtn = "0.396 0.490 0.235 1";
        private const string ColGreenTxt = "0.784 0.922 0.502 1";
        private const string ColGrayBtn = "0.255 0.251 0.235 1";
        private const string ColBlueBtn = "0.243 0.373 0.494 1";
        private const string ColBlueTxt = "0.740 0.870 1 1";
        private const string ColRedBtn = "0.620 0.212 0.161 1";
        private const string ColRedHot = "0.800 0.160 0.110 1";
        private const string ColRedTxt = "1 0.820 0.780 1";
        private const string ColRedTimer = "1 0.330 0.270 1";
        private const string ColOrangeTxt = "1 0.700 0.350 1";
        private const string ColDisabledBtn = "0.170 0.165 0.155 1";
        private const string ColDisabledTxt = "0.450 0.430 0.400 1";
        private const string ColGreenDot = "0.450 0.780 0.320 1";
        private const string ColGrayDot = "0.400 0.400 0.380 1";
        private const string ColGold = "0.937 0.765 0.302 1";
        private const string ColSilver = "0.780 0.800 0.820 1";
        private const string ColBronze = "0.800 0.520 0.300 1";
        private const string ColSegPvp = "0.360 0.600 0.850 1";
        private const string ColSegHold = "0.450 0.700 0.300 1";
        private const string ColSegAdmin = "0.800 0.680 0.300 1";
        private const string ColSegOther = "0.450 0.440 0.420 1";

        // Размеры окна в единицах интерфейса Rust (опорное разрешение 1280×720).
        private const float WinW = 1040f;
        private const float WinH = 620f;
        private const float HeaderH = 56f;
        private const float SideW = 210f;
        private const float ContentX = 222f;
        private const float ContentY = 68f;
        private const float CW = 806f;
        private const float CH = 540f;

        private const string ClanWindow = UiRoot + ".Window";
        private const string ClanHeaderDyn = ClanWindow + ".HeaderDyn";
        private const string ClanNav = ClanWindow + ".Nav";
        private const string ClanDynamic = ClanWindow + ".Dyn";
        private const string AdminWindow = AdminUiRoot + ".Window";
        private const string AdminHeaderDyn = AdminWindow + ".HeaderDyn";
        private const string AdminNav = AdminWindow + ".Nav";
        private const string AdminDynamic = AdminWindow + ".Dyn";

        // Иконки — настоящие предметы Rust. Если предмета нет, берётся следующий из списка.
        private static readonly string[] IcoPoints = { "scrap" };
        private static readonly string[] IcoRank = { "trophy", "skull.trophy", "targeting.computer" };
        private static readonly string[] IcoCapture = { "electric.fuelgenerator.small" };
        private static readonly string[] IcoPvp = { "rifle.ak" };
        private static readonly string[] IcoMembers = { "hazmatsuit", "hoodie" };
        private static readonly string[] IcoOverview = { "map" };
        private static readonly string[] IcoHelp = { "note" };
        private static readonly string[] IcoGreen = { "keycard_green" };
        private static readonly string[] IcoManage = { "hammer" };
        private static readonly string[] IcoFuel = { "lowgradefuel" };
        private static readonly string[] IcoHold = { "cupboard.tool" };
        private static readonly string[] IcoStats = { "targeting.computer", "computerstation" };
        private static readonly string[] IcoSettings = { "wiretool", "electric.switch" };
        private static readonly string[] IcoAdmin = { "cctv.camera", "hammer" };
        private static readonly string[] IcoSign = { "sign.wooden.small", "note" };
        private static readonly string[] IcoInvite = { "note" };

        private readonly Dictionary<string, int> _itemIdCache = new Dictionary<string, int>();
        private readonly Dictionary<ulong, ConfirmState> _confirmations = new Dictionary<ulong, ConfirmState>();
        private readonly Dictionary<ulong, string> _adminSearch = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, int> _adminLogPages = new Dictionary<ulong, int>();

        private class ConfirmState
        {
            public string Key;
            public long Until;
        }

        private class NavTab
        {
            public string Key;
            public string Title;
            public string[] Icons;
            public bool Badge;

            public NavTab(string key, string title, string[] icons, bool badge = false)
            {
                Key = key;
                Title = title;
                Icons = icons;
                Badge = badge;
            }
        }

        #region GUI: подтверждения и общие действия

        private bool ConfirmAction(BasePlayer player, string key)
        {
            if (player == null)
                return false;
            ulong id = player.userID;
            long now = UnixNow();
            ConfirmState state;
            if (_confirmations.TryGetValue(id, out state) && state != null && state.Key == key && state.Until >= now)
            {
                _confirmations.Remove(id);
                return true;
            }

            _confirmations[id] = new ConfirmState { Key = key, Until = now + 10 };
            return false;
        }

        private bool IsConfirming(BasePlayer player, string key)
        {
            if (player == null)
                return false;
            ulong id = player.userID;
            ConfirmState state;
            return _confirmations.TryGetValue(id, out state) && state != null && state.Key == key && state.Until >= UnixNow();
        }

        private void CloseClanUi(BasePlayer player)
        {
            if (player == null)
                return;
            ulong id = player.userID;
            CuiHelper.DestroyUi(player, UiRoot);
            _clanUiOpen.Remove(id);
            _openTabs.Remove(id);
            _clanInfoPanels.Remove(id);
            _clanNavSignatures.Remove(id);
            _clanHeaderSignatures.Remove(id);
        }

        private void CloseClanUiFor(ulong id)
        {
            BasePlayer player = BasePlayer.FindByID(id);
            if (player != null && player.IsConnected)
            {
                CloseClanUi(player);
                return;
            }

            _clanUiOpen.Remove(id);
            _openTabs.Remove(id);
            _clanInfoPanels.Remove(id);
        }

        private void CloseAdminUi(BasePlayer player)
        {
            if (player == null)
                return;
            ulong id = player.userID;
            CuiHelper.DestroyUi(player, AdminUiRoot);
            _adminUiOpen.Remove(id);
        }

        private void ReopenClanUi(BasePlayer player)
        {
            if (player == null)
                return;
            ulong id = player.userID;
            string tab;
            if (!_openTabs.TryGetValue(id, out tab))
                tab = "main";
            OpenClanUi(player, tab);
        }

        private readonly Dictionary<ulong, string> _liveMajorSig = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, long> _liveRefreshAt = new Dictionary<ulong, long>();

        // Перерисовываем открытую вкладку только если что-то реально поменялось:
        // сразу — при смене владельца/статуса, а прогресс — не чаще раза в 20 секунд.
        private void RefreshLiveViews()
        {
            long now = UnixNow();
            string major = string.Join("|", _runtimeTerritories.Values.Select(t =>
                t.Config.Id + ":" + t.State.OwnerTag + ":" + t.State.CapturingTag + ":" + t.State.Status + ":" + (t.State.CooldownUntilUnix > now)).ToArray());
            string progress = string.Join("|", _runtimeTerritories.Values.Select(t => CapturePercent(t.State).ToString()).ToArray());
            string full = major + "#" + progress;

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected)
                    continue;
                ulong id = player.userID;

                string tab;
                bool clanView = _clanUiOpen.Contains(id) && _openTabs.TryGetValue(id, out tab) && (tab == "territory" || (tab == "main" && GetClan(id) != null));
                string mode;
                bool adminView = _adminUiOpen.Contains(id) && _adminUiModes.TryGetValue(id, out mode) && mode == "territories";
                if (!clanView && !adminView)
                {
                    _liveMajorSig.Remove(id);
                    continue;
                }

                string previous;
                _liveMajorSig.TryGetValue(id, out previous);
                long last;
                _liveRefreshAt.TryGetValue(id, out last);
                string previousMajor = previous == null ? null : previous.Split('#')[0];
                bool majorChanged = previousMajor != major;
                bool progressChanged = previous != full;
                if (!majorChanged && (!progressChanged || now - last < 20))
                    continue;

                _liveMajorSig[id] = full;
                _liveRefreshAt[id] = now;
                if (clanView)
                    RefreshClanContent(player);
                else
                    RefreshAdminContent(player);
            }
        }

        #endregion

        #region GUI: окно игрока

        private void OpenClanUi(BasePlayer player, string tab)
        {
            if (player == null || !player.IsConnected || !CanAccessSystem(player)) return;
            ulong userId = player.userID;

            if (_adminUiOpen.Contains(userId))
                CloseAdminUi(player);

            ClanData clan = GetClan(userId);
            tab = NormalizeClanTab(player, clan, tab);
            if (tab != "main")
                _clanInfoPanels.Remove(userId);
            _openTabs[userId] = tab;

            CuiElementContainer ui = new CuiElementContainer();
            if (!_clanUiOpen.Contains(userId))
            {
                CuiHelper.DestroyUi(player, UiRoot);
                BuildWindowShell(ui, UiRoot, ClanWindow);
                _clanUiOpen.Add(userId);
            }
            else
            {
                CuiHelper.DestroyUi(player, ClanHeaderDyn);
                CuiHelper.DestroyUi(player, ClanNav);
                CuiHelper.DestroyUi(player, ClanDynamic);
            }

            DrawClanHeader(ui, player, clan);
            DrawClanNav(ui, player, clan, tab);
            UPanel(ui, ClanWindow, ClanDynamic, "0 0 0 0", ContentX, ContentY, CW, CH);
            DrawClanTab(ui, player, clan, tab);
            CuiHelper.AddUi(player, ui);
        }

        private void RefreshClanContent(BasePlayer player)
        {
            if (player == null || !player.IsConnected)
                return;
            ulong userId = player.userID;
            if (!_clanUiOpen.Contains(userId))
                return;
            if (!CanAccessSystem(player))
            {
                CloseClanUi(player);
                return;
            }

            string tab;
            if (!_openTabs.TryGetValue(userId, out tab))
                tab = "main";
            ClanData clan = GetClan(userId);
            tab = NormalizeClanTab(player, clan, tab);

            CuiHelper.DestroyUi(player, ClanDynamic);
            CuiElementContainer ui = new CuiElementContainer();
            UPanel(ui, ClanWindow, ClanDynamic, "0 0 0 0", ContentX, ContentY, CW, CH);
            DrawClanTab(ui, player, clan, tab);
            CuiHelper.AddUi(player, ui);
        }

        private string NormalizeClanTab(BasePlayer player, ClanData clan, string tab)
        {
            tab = string.IsNullOrEmpty(tab) ? "main" : tab.ToLowerInvariant();

            if (tab != "main" && tab != "members" && tab != "territory" && tab != "ranking" &&
                tab != "help" && tab != "green" && tab != "manage")
                tab = "main";

            if (clan == null && tab != "main" && tab != "ranking" && tab != "help")
                tab = "main";
            if (tab == "green" && (clan == null || !CanManageGreenTeam(clan, player.userID)))
                tab = "main";
            if (tab == "manage" && (clan == null || !IsLeader(clan, player.userID)))
                tab = "main";

            return tab;
        }

        private List<NavTab> GetClanNavigationTabs(BasePlayer player, ClanData clan)
        {
            List<NavTab> tabs = new List<NavTab>();
            if (clan == null)
            {
                ClanInvite invite;
                bool invited = _data.Invites.TryGetValue(player.userID, out invite) && invite != null && invite.ExpiresUnix > UnixNow();
                tabs.Add(new NavTab("main", "ГЛАВНАЯ", IcoOverview, invited));
            }
            else
            {
                bool capturing = _runtimeTerritories.Values.Any(t => !string.IsNullOrEmpty(t.State.CapturingTag));
                tabs.Add(new NavTab("main", "ОБЗОР", IcoOverview));
                tabs.Add(new NavTab("members", "СОСТАВ", IcoMembers));
                tabs.Add(new NavTab("territory", "ТЕРРИТОРИИ", IcoCapture, capturing));
            }

            tabs.Add(new NavTab("ranking", "РЕЙТИНГ", IcoRank));

            if (clan != null && CanManageGreenTeam(clan, player.userID))
                tabs.Add(new NavTab("green", "ЗЕЛЁНКА", IcoGreen));
            if (clan != null && IsLeader(clan, player.userID))
                tabs.Add(new NavTab("manage", "УПРАВЛЕНИЕ", IcoManage));

            tabs.Add(new NavTab("help", "СПРАВКА", IcoHelp));
            return tabs;
        }

        private void BuildWindowShell(CuiElementContainer c, string root, string window)
        {
            c.Add(new CuiPanel
            {
                Image = { Color = "0.040 0.040 0.035 0.86", Material = MatBlur, FadeIn = 0.1f },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true
            }, "Overlay", root);

            c.Add(new CuiElement
            {
                Name = window,
                Parent = root,
                Components =
                {
                    new CuiImageComponent { Color = ColWindow, Sprite = SpriteTile, FadeIn = 0.1f },
                    new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = F(-WinW / 2f) + " " + F(-WinH / 2f), OffsetMax = F(WinW / 2f) + " " + F(WinH / 2f) }
                }
            });

            UPanel(c, window, null, ColHeader, 0, 0, WinW, HeaderH, SpriteTile);
            UPanel(c, window, null, ColAccent, 0, 0, 4, HeaderH);
            UPanel(c, window, null, ColBorder, 0, HeaderH, WinW, 1);
            UPanel(c, window, null, ColPanelDark, 0, HeaderH + 1, SideW, WinH - HeaderH - 1, SpriteTile);
            UPanel(c, window, null, ColBorder, SideW, HeaderH + 1, 1, WinH - HeaderH - 1);
            UFrame(c, window, ColBorder, 0, 0, WinW, WinH);
        }

        private void DrawClanHeader(CuiElementContainer c, BasePlayer player, ClanData clan)
        {
            UPanel(c, ClanWindow, ClanHeaderDyn, "0 0 0 0", 0, 0, WinW, HeaderH);
            string p = ClanHeaderDyn;
            UIcon(c, p, IcoMembers, 18, 10, 36);
            UText(c, p, "КЛАНЫ", 20, ColText, TextAnchor.MiddleLeft, 64, 5, 400, 26, true);

            string sub = clan == null
                ? "Клановая система сервера"
                : string.Format("<color={0}>[{1}]</color>  {2}  <color=#6b665e>•</color>  {3}", TagColor, clan.Tag, EscapeRichText(clan.DisplayName), GetRoleName(clan, player.userID));
            UText(c, p, sub, 12, ColTextDim, TextAnchor.MiddleLeft, 64, 30, 640, 20);

            float x = WinW - 12f - 40f;
            UCloseButton(c, p, "rrclan.ui close", x, 8f);
            if (HasAdminPermission(player))
                UButton(c, p, "rrclan.adminui open", ColGrayBtn, "АДМИН-ПАНЕЛЬ", 12, ColText, x - 8f - 140f, 8f, 140f, 40f);
        }

        private void DrawClanNav(CuiElementContainer c, BasePlayer player, ClanData clan, string active)
        {
            float navH = WinH - HeaderH - 1f;
            UPanel(c, ClanWindow, ClanNav, "0 0 0 0", 0, HeaderH + 1f, SideW, navH);

            float y = 12f;
            foreach (NavTab tab in GetClanNavigationTabs(player, clan))
            {
                DrawNavButton(c, ClanNav, "rrclan.ui tab " + tab.Key, tab, tab.Key == active, y);
                y += 46f;
            }

            float cardY = navH - 72f;
            UPanel(c, ClanNav, null, ColBorder, 10, cardY - 10f, SideW - 20f, 1);
            UAvatar(c, ClanNav, player.userID, 12, cardY, 52);
            UText(c, ClanNav, EscapeRichText(Truncate(player.displayName, 16)), 13, ColText, TextAnchor.MiddleLeft, 74, cardY + 4f, SideW - 84f, 22, true);
            string line = clan == null
                ? "Без клана"
                : string.Format("<color={0}>[{1}]</color> {2}", TagColor, clan.Tag, GetRoleName(clan, player.userID));
            UText(c, ClanNav, line, 11, ColTextDim, TextAnchor.MiddleLeft, 74, cardY + 26f, SideW - 84f, 20);
        }

        private void DrawNavButton(CuiElementContainer c, string parent, string command, NavTab tab, bool active, float y)
        {
            string name = parent + ".Tab." + tab.Key;
            float w = SideW - 20f;
            UButton(c, parent, command, active ? ColSlotSolid : "1 1 1 0.025", string.Empty, 1, ColText, 10, y, w, 40, name);
            if (active)
                UPanel(c, name, null, ColAccent, 0, 0, 3, 40);
            UIcon(c, name, tab.Icons, 12, 6, 28);
            UText(c, name, tab.Title, 13, active ? ColText : ColTextDim, TextAnchor.MiddleLeft, 50, 0, w - 70f, 40, true);
            if (tab.Badge)
                UPanel(c, name, null, ColAccent, w - 18f, 16, 8, 8, SpriteCircle);
        }

        private void DrawClanTab(CuiElementContainer c, BasePlayer player, ClanData clan, string tab)
        {
            if (clan == null)
            {
                if (tab == "ranking") DrawRankingTab(c, player, null);
                else if (tab == "help") DrawHelpTab(c, player);
                else DrawNoClanTab(c, player);
                return;
            }

            switch (tab)
            {
                case "members": DrawMembersTab(c, player, clan); break;
                case "territory": DrawTerritoryTab(c, player, clan); break;
                case "ranking": DrawRankingTab(c, player, clan); break;
                case "help": DrawHelpTab(c, player); break;
                case "green": DrawGreenTeamTab(c, player, clan); break;
                case "manage": DrawManagementTab(c, player, clan); break;
                default: DrawMainTab(c, player, clan); break;
            }
        }

        #endregion

        #region GUI: вкладки игрока

        private void DrawNoClanTab(CuiElementContainer c, BasePlayer player)
        {
            string p = ClanDynamic;
            ulong userId = player.userID;
            DrawTitle(c, p, "КЛАНОВАЯ СИСТЕМА", "Создайте свой клан или вступите по приглашению");

            float y = 62f;
            ClanInvite invite;
            if (_data.Invites.TryGetValue(userId, out invite) && invite != null && invite.ExpiresUnix > UnixNow())
            {
                ClanData inviting = GetClanByTag(invite.ClanTag);
                string box = p + ".Invite";
                UPanel(c, p, box, ColPanel, 0, y, CW, 64, SpriteTile);
                UPanel(c, box, null, ColAccent, 0, 0, 3, 64);
                UIcon(c, box, IcoInvite, 14, 12, 40);
                UText(c, box, string.Format("ВАС ПРИГЛАШАЮТ В КЛАН <color={0}>[{1}]</color> {2}", TagColor, EscapeRichText(invite.ClanTag), inviting == null ? string.Empty : EscapeRichText(inviting.DisplayName)), 14, ColText, TextAnchor.MiddleLeft, 66, 8, 470, 24, true);
                UText(c, box, string.Format("Пригласил: {0}  •  приглашение сгорит через {1}", EscapeRichText(GetKnownName(inviting, invite.InviterId)), FormatDuration(invite.ExpiresUnix - UnixNow())), 11, ColTextDim, TextAnchor.MiddleLeft, 66, 32, 470, 22);
                UButton(c, box, "rrclan.ui accept", ColGreenBtn, "ПРИНЯТЬ", 13, ColGreenTxt, CW - 260f, 14, 120, 36);
                UButton(c, box, "rrclan.ui decline", ColRedBtn, "ОТКЛОНИТЬ", 13, ColRedTxt, CW - 132f, 14, 120, 36);
                y += 76f;
            }

            float stepW = (CW - 24f) / 3f;
            float stepH = 146f;
            bool greenReady;
            string greenText;
            EvaluateGreenTeamReadiness(player, out greenReady, out greenText);
            DrawStepCard(c, p, 1, IcoGreen, "ЗЕЛЁНАЯ КОМАНДА",
                "Откройте меню команды Rust и создайте команду. В ней должны быть только вы — участников позовёте после создания клана.",
                greenText, greenReady ? ColGreenTxt : ColRedTimer, 0, y, stepW, stepH);
            DrawStepCard(c, p, 2, IcoSign, "ТЕГ КЛАНА",
                string.Format("От {0} до {1} символов: латинские буквы A–Z и цифры. Тег будет виден в чате перед ником каждого участника.", _config.Clan.MinTagLength, _config.Clan.MaxTagLength),
                null, null, stepW + 12f, y, stepW, stepH);
            DrawStepCard(c, p, 3, IcoMembers, "СОСТАВ",
                string.Format("До {0} участников. Приглашайте игроков во вкладке СОСТАВ, а затем добавьте их в зелёную команду.", _config.Clan.MaxMembers),
                null, null, (stepW + 12f) * 2f, y, stepW, stepH);
            y += stepH + 12f;

            string create = p + ".Create";
            float createH = 92f;
            UPanel(c, p, create, ColPanel, 0, y, CW, createH, SpriteTile);
            UPanel(c, create, null, ColAccent, 0, 0, 3, createH);
            UText(c, create, "СОЗДАТЬ КЛАН", 15, ColText, TextAnchor.MiddleLeft, 18, 14, 330, 24, true);
            UText(c, create, "Впишите тег в поле справа и нажмите <b>Enter</b>.\nЕсли поле не работает — кнопка «ВВЕСТИ В ЧАТ».", 11, ColTextDim, TextAnchor.UpperLeft, 18, 42, 360, 40);
            UText(c, create, "ТЕГ", 10, ColTextMute, TextAnchor.MiddleLeft, 400, 10, 200, 14, true);
            DrawInput(c, create, create + ".Input", "rrclan.create", Math.Max(2, _config.Clan.MaxTagLength), 400, 28, 220, 40);
            UButton(c, create, "rrclan.ui begincreate", ColGrayBtn, "ВВЕСТИ В ЧАТ", 12, ColText, CW - 16f - 150f, 28, 150, 40);
            y += createH + 12f;

            List<ClanData> top = GetLeaderboard().Take(3).ToList();
            if (top.Count > 0 && CH - y >= 76f)
            {
                UText(c, p, "ЛИДЕРЫ ВАЙПА", 11, ColTextDim, TextAnchor.MiddleLeft, 0, y, 300, 18, true);
                y += 22f;
                float cardW = (CW - 24f) / 3f;
                float cardH = Mathf.Min(56f, CH - y);
                for (int i = 0; i < top.Count; i++)
                {
                    string card = p + ".Top" + i;
                    UPanel(c, p, card, ColPanel, i * (cardW + 12f), y, cardW, cardH, SpriteTile);
                    UPanel(c, card, null, RankColor(i + 1), 10, (cardH - 32f) / 2f, 32, 32, SpriteCircle);
                    UText(c, card, (i + 1).ToString(), 14, "0.1 0.1 0.1 1", TextAnchor.MiddleCenter, 10, (cardH - 32f) / 2f, 32, 32, true);
                    UText(c, card, string.Format("<color={0}>[{1}]</color> {2}", TagColor, top[i].Tag, EscapeRichText(Truncate(top[i].DisplayName, 14))), 13, ColText, TextAnchor.MiddleLeft, 52, 4, cardW - 60f, cardH / 2f, true);
                    UText(c, card, top[i].Score + " очк.", 11, ColTextDim, TextAnchor.MiddleLeft, 52, cardH / 2f - 2f, cardW - 60f, cardH / 2f);
                }
            }
        }

        private void EvaluateGreenTeamReadiness(BasePlayer player, out bool ready, out string text)
        {
            if (!_config.Clan.RequireGreenTeamBeforeClanCreation)
            {
                ready = true;
                text = "НЕ ОБЯЗАТЕЛЬНА НА ЭТОМ СЕРВЕРЕ";
                return;
            }

            ulong userId = player.userID;
            RelationshipManager.PlayerTeam team = player.currentTeam == 0UL || RelationshipManager.ServerInstance == null
                ? null
                : RelationshipManager.ServerInstance.FindTeam(player.currentTeam);
            if (team == null)
            {
                ready = false;
                text = "НЕ ВЫПОЛНЕНО: команда не создана";
                return;
            }
            if (team.teamLeader != userId)
            {
                ready = false;
                text = "НЕ ВЫПОЛНЕНО: вы не лидер команды";
                return;
            }
            if (team.members.Count != 1)
            {
                ready = false;
                text = "НЕ ВЫПОЛНЕНО: в команде должны быть только вы";
                return;
            }
            if (GetClanByTeamId(team.teamID) != null)
            {
                ready = false;
                text = "НЕ ВЫПОЛНЕНО: команда уже привязана к клану";
                return;
            }

            ready = true;
            text = "ГОТОВО";
        }

        private void DrawStepCard(CuiElementContainer c, string parent, int number, string[] icon, string title, string body, string status, string statusColor, float x, float y, float w, float h)
        {
            string name = parent + ".Step" + number;
            UPanel(c, parent, name, ColPanel, x, y, w, h, SpriteTile);
            UPanel(c, name, null, ColPanelDark, 12, 12, 48, 48, SpriteCircle);
            UIcon(c, name, icon, 20, 20, 32);
            UText(c, name, "ШАГ " + number, 10, ColAccentTxt, TextAnchor.MiddleLeft, 72, 14, w - 84f, 16, true);
            UText(c, name, title, 14, ColText, TextAnchor.MiddleLeft, 72, 30, w - 84f, 22, true);
            float bodyH = h - 70f - (status != null ? 28f : 8f);
            UText(c, name, body, 11, ColTextDim, TextAnchor.UpperLeft, 12, 68, w - 24f, bodyH);
            if (status != null)
                UText(c, name, status, 11, statusColor ?? ColText, TextAnchor.MiddleLeft, 12, h - 28f, w - 24f, 20, true);
        }

        private void DrawMainTab(CuiElementContainer c, BasePlayer player, ClanData clan)
        {
            string p = ClanDynamic;
            ulong userId = player.userID;
            List<ClanData> board = GetLeaderboard();
            int rankIndex = board.FindIndex(x => x.ClanId == clan.ClanId);
            int rank = rankIndex + 1;
            int online = clan.Members.Count(IsOnline);

            DrawTitle(c, p, "ОБЗОР КЛАНА", "Итоги текущего вайпа");

            string id = p + ".Identity";
            float idH = 96f;
            UPanel(c, p, id, ColPanel, 0, 62, CW, idH, SpriteTile);
            UPanel(c, id, null, ColAccent, 0, 0, 3, idH);
            UAvatar(c, id, clan.OwnerId, 14, 12, 72);
            UText(c, id, string.Format("<color={0}>[{1}]</color>  {2}", TagColor, clan.Tag, EscapeRichText(clan.DisplayName)), 22, ColText, TextAnchor.MiddleLeft, 100, 8, 480, 32, true);
            UText(c, id, string.Format("Глава: <color=#e8ded3>{0}</color>    •    Участников: <color=#e8ded3>{1}/{2}</color>    •    В сети: <color=#9fd36b>{3}</color>",
                EscapeRichText(Truncate(GetKnownName(clan, clan.OwnerId), 20)), clan.Members.Count, _config.Clan.MaxMembers, online), 12, ColTextDim, TextAnchor.MiddleLeft, 100, 42, 520, 20);
            UText(c, id, string.Format("Клан создан: <color=#e8ded3>{0}</color>    •    Ваша роль: <color=#e8ded3>{1}</color>", FormatDate(clan.CreatedUnix), GetRoleName(clan, userId)), 12, ColTextDim, TextAnchor.MiddleLeft, 100, 64, 520, 20);
            UText(c, id, "МЕСТО В РЕЙТИНГЕ", 10, ColTextDim, TextAnchor.MiddleRight, CW - 230f, 10, 216, 16, true);
            UText(c, id, rank > 0 ? "#" + rank : "—", 30, RankColor(rank), TextAnchor.MiddleRight, CW - 230f, 26, 216, 40, true);
            UText(c, id, GetRankGapText(board, rankIndex), 11, ColTextDim, TextAnchor.MiddleRight, CW - 290f, 68, 276, 18);

            float sy = 62f + idH + 12f;
            float sh = 84f;
            float sw = (CW - 36f) / 4f;
            DrawStatCard(c, p, "points", IcoPoints, "ОЧКИ", clan.Score.ToString(), ColText, "points", 0, sy, sw, sh);
            DrawStatCard(c, p, "rank", IcoRank, "МЕСТО", rank > 0 ? "#" + rank + " из " + board.Count : "—", RankColor(rank), "rank", (sw + 12f), sy, sw, sh);
            DrawStatCard(c, p, "captures", IcoCapture, "ЗАХВАТЫ", clan.Captures.ToString(), ColText, "captures", (sw + 12f) * 2f, sy, sw, sh);
            DrawStatCard(c, p, "pvp", IcoPvp, "PVP-УБИЙСТВА", clan.Kills.ToString(), ColText, "pvp", (sw + 12f) * 3f, sy, sw, sh);

            float ly = sy + sh + 12f;
            float lh = CH - ly;
            float half = (CW - 12f) / 2f;
            DrawScoreBreakdown(c, p, clan, 0, ly, half, lh);
            DrawClanTerritoriesPanel(c, p, clan, half + 12f, ly, half, lh);

            string selectedInfo;
            if (_clanInfoPanels.TryGetValue(userId, out selectedInfo))
                DrawClanInfoPopup(c, clan, selectedInfo);
        }

        private string GetRankGapText(List<ClanData> board, int index)
        {
            if (index < 0)
                return string.Empty;
            if (index == 0)
                return board[0].Score > 0 ? "Ваш клан лидирует" : "Наберите первые очки";
            int diff = board[index - 1].Score - board[index].Score;
            if (diff <= 0)
                return string.Format("Наравне с #{0}", index);
            return string.Format("До #{0} не хватает {1} очк.", index, diff);
        }

        private void DrawScoreBreakdown(CuiElementContainer c, string parent, ClanData clan, float x, float y, float w, float h)
        {
            string box = parent + ".Breakdown";
            UPanel(c, parent, box, ColPanel, x, y, w, h, SpriteTile);
            UText(c, box, "ИЗ ЧЕГО СЛОЖИЛИСЬ ОЧКИ", 12, ColText, TextAnchor.MiddleLeft, 14, 8, w - 28f, 20, true);

            int other = clan.Score - (clan.KillScore + clan.CaptureScore + clan.HoldScore + clan.AdminScore);
            List<KeyValuePair<int, string>> segments = new List<KeyValuePair<int, string>>
            {
                new KeyValuePair<int, string>(clan.KillScore, ColSegPvp),
                new KeyValuePair<int, string>(clan.CaptureScore, ColAccent),
                new KeyValuePair<int, string>(clan.HoldScore, ColSegHold),
                new KeyValuePair<int, string>(Math.Max(0, clan.AdminScore), ColSegAdmin),
                new KeyValuePair<int, string>(Math.Max(0, other), ColSegOther)
            };
            int positive = segments.Sum(sv => Math.Max(0, sv.Key));
            float barX = 14f, barY = 36f, barW = w - 28f, barH = 12f;
            UPanel(c, box, null, ColTrack, barX, barY, barW, barH);
            if (positive > 0)
            {
                float cursor = barX;
                foreach (KeyValuePair<int, string> segment in segments)
                {
                    if (segment.Key <= 0)
                        continue;
                    float segW = barW * segment.Key / positive;
                    UPanel(c, box, null, segment.Value, cursor, barY, Mathf.Max(1f, segW), barH);
                    cursor += segW;
                }
            }

            float rowY = 58f;
            float rowH = Mathf.Min(30f, (h - 58f - 34f) / 5f);
            DrawBreakdownRow(c, box, IcoPvp, ColSegPvp, "PvP", string.Format("{0} убийств", clan.Kills), clan.KillScore, 14, rowY, w - 28f, rowH);
            rowY += rowH;
            DrawBreakdownRow(c, box, IcoCapture, ColAccent, "Захваты", string.Format("{0} успешных", clan.Captures), clan.CaptureScore, 14, rowY, w - 28f, rowH);
            rowY += rowH;
            DrawBreakdownRow(c, box, IcoHold, ColSegHold, "Удержание", "территорий", clan.HoldScore, 14, rowY, w - 28f, rowH);
            rowY += rowH;
            if (clan.AdminScore != 0)
            {
                DrawBreakdownRow(c, box, IcoAdmin, ColSegAdmin, "Администратор", "корректировка", clan.AdminScore, 14, rowY, w - 28f, rowH);
                rowY += rowH;
            }
            if (other != 0)
                DrawBreakdownRow(c, box, IcoPoints, ColSegOther, "Ранее", "до обновления плагина", other, 14, rowY, w - 28f, rowH);

            UPanel(c, box, null, ColBorder, 14, h - 32f, w - 28f, 1);
            UText(c, box, "ИТОГО", 12, ColTextDim, TextAnchor.MiddleLeft, 14, h - 30f, 120, 26, true);
            UText(c, box, clan.Score + " очк.", 14, ColText, TextAnchor.MiddleRight, w - 160f, h - 30f, 146, 26, true);
        }

        private void DrawBreakdownRow(CuiElementContainer c, string parent, string[] icon, string color, string title, string hint, int value, float x, float y, float w, float h)
        {
            UPanel(c, parent, null, color, x, y + h / 2f - 4f, 8, 8);
            UIcon(c, parent, icon, x + 14f, y + (h - 22f) / 2f, 22);
            UText(c, parent, string.Format("{0}  <color=#736d64>{1}</color>", title, hint), 12, ColText, TextAnchor.MiddleLeft, x + 42f, y, w - 140f, h);
            string valueText = (value > 0 ? "+" : string.Empty) + value;
            UText(c, parent, valueText, 13, value < 0 ? ColRedTimer : ColText, TextAnchor.MiddleRight, x + w - 100f, y, 100, h, true);
        }

        private void DrawClanTerritoriesPanel(CuiElementContainer c, string parent, ClanData clan, float x, float y, float w, float h)
        {
            string box = parent + ".Lands";
            UPanel(c, parent, box, ColPanel, x, y, w, h, SpriteTile);
            UText(c, box, "ТЕРРИТОРИИ КЛАНА", 12, ColText, TextAnchor.MiddleLeft, 14, 8, w - 28f, 20, true);

            float rowY = 36f;
            RuntimeTerritory active = _runtimeTerritories.Values.FirstOrDefault(t => string.Equals(t.State.CapturingTag, clan.Tag, StringComparison.OrdinalIgnoreCase));
            if (active != null)
            {
                string row = box + ".Active";
                UPanel(c, box, row, ColAccentSoft, 14, rowY, w - 28f, 66);
                UPanel(c, row, null, ColAccent, 0, 0, 3, 66);
                UIcon(c, row, TerritoryIcon(active.Config), 10, 10, 30);
                UText(c, row, "ИДЁТ ЗАХВАТ: " + EscapeRichText(active.Config.DisplayName.ToUpperInvariant()), 12, ColText, TextAnchor.MiddleLeft, 48, 6, w - 90f, 18, true);
                UText(c, row, GetCaptureStatusShort(active), 11, ColText, TextAnchor.MiddleLeft, 48, 24, w - 90f, 16);
                DrawBar(c, row, 48, 44, w - 28f - 60f, 10, CaptureRatio(active.State), ColAccent);
                UText(c, row, CapturePercent(active.State) + "%", 11, ColText, TextAnchor.MiddleRight, w - 28f - 60f, 2, 50, 18, true);
                rowY += 74f;
            }

            List<RuntimeTerritory> owned = _runtimeTerritories.Values
                .Where(t => string.Equals(t.State.OwnerTag, clan.Tag, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.Config.DisplayName)
                .ToList();

            if (owned.Count == 0 && active == null)
            {
                UIcon(c, box, IcoCapture, w / 2f - 24f, 46, 48);
                UText(c, box, "Клан пока не владеет территориями.\nЗахватите РТ, чтобы получать очки за удержание.", 12, ColTextDim, TextAnchor.UpperCenter, 14, 100, w - 28f, 40);
                UButton(c, box, "rrclan.ui tab territory", ColBlueBtn, "ОТКРЫТЬ ТЕРРИТОРИИ", 12, ColBlueTxt, w / 2f - 90f, 146, 180, 34);
            }

            long now = UnixNow();
            foreach (RuntimeTerritory territory in owned)
            {
                if (rowY + 50f > h - 30f)
                    break;
                string row = box + ".Own." + territory.Config.Id;
                UPanel(c, box, row, ColRowA, 14, rowY, w - 28f, 46);
                UPanel(c, row, null, ColGreenBtn, 0, 0, 3, 46);
                UIcon(c, row, TerritoryIcon(territory.Config), 10, 8, 30);
                UText(c, row, EscapeRichText(territory.Config.DisplayName.ToUpperInvariant()), 12, ColText, TextAnchor.MiddleLeft, 48, 4, w - 200f, 20, true);
                int inside = CountClanMembersInside(clan.Tag, territory);
                UText(c, row, string.Format("Ваших на территории: {0}", inside), 11, ColTextDim, TextAnchor.MiddleLeft, 48, 24, w - 200f, 18);
                long next = territory.State.NextHoldRewardUnix - now;
                UText(c, row, next > 0 ? "+" + _config.Score.HoldPoints + " через " + FormatDuration(next) : "начисление скоро", 11, ColGreenTxt, TextAnchor.MiddleRight, w - 28f - 170f, 0, 160, 46, true);
                rowY += 52f;
            }

            UText(c, box, string.Format("Удержание: +{0} очк. каждые {1}, если на РТ есть хотя бы {2} уч.", _config.Score.HoldPoints, FormatMinutes(_config.Score.HoldRewardIntervalSeconds), Math.Max(1, _config.Score.MinimumMembersInsideForHoldReward)),
                10, ColTextMute, TextAnchor.MiddleLeft, 14, h - 26f, w - 28f, 20);
        }

        private void DrawMembersTab(CuiElementContainer c, BasePlayer player, ClanData clan)
        {
            string p = ClanDynamic;
            ulong userId = player.userID;
            bool leader = IsLeader(clan, userId);
            int online = clan.Members.Count(IsOnline);
            DrawTitle(c, p, "СОСТАВ", string.Format("{0}/{1} участников  •  в сети {2}", clan.Members.Count, _config.Clan.MaxMembers, online));

            if (!leader)
            {
                bool confirm = IsConfirming(player, "leave");
                UButton(c, p, "rrclan.ui leave", confirm ? ColRedHot : ColRedBtn, confirm ? "ТОЧНО ВЫЙТИ?" : "ВЫЙТИ ИЗ КЛАНА", 12, ColRedTxt, CW - 170f, 8, 170, 34);
            }

            RelationshipManager.PlayerTeam team = GetManualGreenTeam(clan);
            float listW = leader ? 532f : CW;
            List<ulong> members = clan.Members.OrderBy(id => GetRoleSort(clan, id)).ThenBy(id => GetKnownName(clan, id)).ToList();
            float rowH = 58f;
            float viewY = 62f;
            float viewH = CH - viewY;
            string scroll = p + ".Members";
            UScroll(c, p, scroll, 0, viewY, listW, viewH, members.Count * (rowH + 4f));
            float rowW = listW - 12f;
            for (int i = 0; i < members.Count; i++)
                DrawMemberRow(c, scroll, player, clan, team, members[i], i, i * (rowH + 4f), rowW, rowH, leader);

            if (leader)
                DrawInvitePanel(c, p, player, clan, listW + 12f, viewY, CW - listW - 12f, viewH);
        }

        private void DrawMemberRow(CuiElementContainer c, string parent, BasePlayer viewer, ClanData clan, RelationshipManager.PlayerTeam team, ulong id, int index, float y, float w, float h, bool leaderView)
        {
            string row = parent + ".M" + index;
            UPanel(c, parent, row, index % 2 == 0 ? ColRowA : ColRowB, 0, y, w, h, SpriteTile);
            if (id == clan.OwnerId)
                UPanel(c, row, null, ColGold, 0, 0, 3, h);
            else if (id == clan.DeputyId)
                UPanel(c, row, null, ColBlueTxt, 0, 0, 3, h);

            UAvatar(c, row, id, 10, 8, 42);
            UPanel(c, row, null, IsOnline(id) ? ColGreenDot : ColGrayDot, 44, 40, 10, 10, SpriteCircle);
            UText(c, row, EscapeRichText(Truncate(GetKnownName(clan, id), 22)), 14, ColText, TextAnchor.MiddleLeft, 62, 6, 250, 22, true);

            string role = GetRoleName(clan, id);
            float chipW = 14f + role.Length * 7f;
            string chipBg = id == clan.OwnerId ? "0.45 0.36 0.12 1" : id == clan.DeputyId ? "0.20 0.32 0.45 1" : ColSlotSolid;
            string chipFg = id == clan.OwnerId ? ColGold : id == clan.DeputyId ? ColBlueTxt : ColTextDim;
            DrawChip(c, row, role, chipBg, chipFg, 62, 32, chipW, 18);

            int kills = clan.PlayerKills != null && clan.PlayerKills.ContainsKey(id) ? clan.PlayerKills[id] : 0;
            float infoX = 62f + chipW + 8f;
            UIcon(c, row, IcoPvp, infoX, 31, 20);
            UText(c, row, kills.ToString(), 11, ColTextDim, TextAnchor.MiddleLeft, infoX + 22f, 32, 40, 18, true);
            if (team != null && team.members.Contains(id))
            {
                UIcon(c, row, IcoGreen, infoX + 56f, 31, 20);
                UText(c, row, "в зелёнке", 11, ColGreenTxt, TextAnchor.MiddleLeft, infoX + 78f, 32, 80, 18);
            }

            if (leaderView && id != clan.OwnerId)
            {
                float bw = 92f, bh = 24f;
                float bx = w - 8f - bw * 2f - 4f;
                bool deputy = clan.DeputyId == id;
                bool confirmTransfer = IsConfirming(viewer, "transfer:" + id);
                bool confirmKick = IsConfirming(viewer, "kick:" + id);
                UButton(c, row, "rrclan.manage role " + id, ColGrayBtn, "СМЕНИТЬ РОЛЬ", 10, ColText, bx, 5, bw, bh);
                UButton(c, row, "rrclan.manage deputy " + id, deputy ? "0.30 0.25 0.12 1" : ColBlueBtn, deputy ? "СНЯТЬ ЗАМА" : "В ЗАМЕСТИТЕЛИ", 10, deputy ? ColGold : ColBlueTxt, bx + bw + 4f, 5, bw, bh);
                UButton(c, row, "rrclan.manage transfer " + id, confirmTransfer ? ColRedHot : "0.33 0.25 0.38 1", confirmTransfer ? "ТОЧНО?" : "ПЕРЕДАТЬ ГЛАВУ", 10, ColText, bx, 31, bw, bh);
                UButton(c, row, "rrclan.manage kick " + id, confirmKick ? ColRedHot : ColRedBtn, confirmKick ? "ТОЧНО?" : "ИСКЛЮЧИТЬ", 10, ColRedTxt, bx + bw + 4f, 31, bw, bh);
            }
            else
            {
                UText(c, row, IsOnline(id) ? "В СЕТИ" : "НЕ В СЕТИ", 11, IsOnline(id) ? ColGreenTxt : ColTextMute, TextAnchor.MiddleRight, w - 140f, 0, 128, h, true);
            }
        }

        private void DrawInvitePanel(CuiElementContainer c, string parent, BasePlayer player, ClanData clan, float x, float y, float w, float h)
        {
            string box = parent + ".Invite";
            UPanel(c, parent, box, ColPanel, x, y, w, h, SpriteTile);
            UText(c, box, "ПРИГЛАСИТЬ ИГРОКА", 13, ColText, TextAnchor.MiddleLeft, 12, 8, w - 24f, 20, true);

            if (clan.Members.Count >= _config.Clan.MaxMembers)
            {
                UIcon(c, box, IcoMembers, w / 2f - 24f, 60, 48);
                UText(c, box, string.Format("Клан заполнен: {0}/{1}.\nЧтобы пригласить кого-то, освободите место.", clan.Members.Count, _config.Clan.MaxMembers), 12, ColTextDim, TextAnchor.UpperCenter, 12, 116, w - 24f, 50);
                return;
            }

            UText(c, box, "Ник или SteamID, затем Enter:", 11, ColTextDim, TextAnchor.MiddleLeft, 12, 30, w - 24f, 16);
            DrawInput(c, box, box + ".Input", "rrclan.ui invitename", 32, 12, 50, w - 24f, 34);
            UText(c, box, "ИГРОКИ ОНЛАЙН БЕЗ КЛАНА", 10, ColTextMute, TextAnchor.MiddleLeft, 12, 94, w - 24f, 16, true);

            ulong selfId = player.userID;
            long now = UnixNow();
            List<BasePlayer> candidates = BasePlayer.activePlayerList
                .Where(target => target != null && target.IsConnected && target != player && CanAccessSystem(target) && GetClan(target.userID) == null)
                .OrderBy(target => target.displayName)
                .Take(25)
                .ToList();

            if (candidates.Count == 0)
            {
                UText(c, box, "Сейчас нет игроков без клана.", 11, ColTextMute, TextAnchor.UpperLeft, 12, 116, w - 24f, 20);
                return;
            }

            string scroll = box + ".List";
            float rowH = 40f;
            UScroll(c, box, scroll, 8, 114, w - 16f, h - 122f, candidates.Count * (rowH + 4f));
            float rowW = w - 28f;
            for (int i = 0; i < candidates.Count; i++)
            {
                BasePlayer target = candidates[i];
                ulong targetId = target.userID;
                ClanInvite invite;
                bool invited = _data.Invites.TryGetValue(targetId, out invite) && invite != null && invite.ExpiresUnix > now &&
                               string.Equals(invite.ClanTag, clan.Tag, StringComparison.OrdinalIgnoreCase);
                string row = scroll + ".C" + i;
                UPanel(c, scroll, row, i % 2 == 0 ? ColRowA : ColRowB, 0, i * (rowH + 4f), rowW, rowH);
                UAvatar(c, row, targetId, 6, 6, 28);
                UText(c, row, EscapeRichText(Truncate(target.displayName, 14)), 12, ColText, TextAnchor.MiddleLeft, 40, 0, rowW - 140f, rowH, true);
                if (invited)
                    UButton(c, row, string.Empty, ColDisabledBtn, "ОТПРАВЛЕНО", 10, ColDisabledTxt, rowW - 96f, 7, 90, 26);
                else
                    UButton(c, row, "rrclan.ui invite " + targetId, ColGreenBtn, "ПРИГЛАСИТЬ", 10, ColGreenTxt, rowW - 96f, 7, 90, 26);
            }
        }

        private void DrawTerritoryTab(CuiElementContainer c, BasePlayer player, ClanData clan)
        {
            string p = ClanDynamic;
            DrawTitle(c, p, "ТЕРРИТОРИИ", "Захватывайте РТ генератором на ТНК и получайте очки за удержание");

            List<RuntimeTerritory> list = _runtimeTerritories.Values.OrderBy(t => t.Config.DisplayName).ToList();
            float infoH = 44f;
            float viewY = 62f;
            float viewH = CH - viewY - infoH - 10f;
            float rowH = 118f;

            if (list.Count == 0)
            {
                UIcon(c, p, IcoCapture, CW / 2f - 32f, 150, 64);
                UText(c, p, "Территории для захвата пока не включены администратором.", 13, ColTextDim, TextAnchor.MiddleCenter, 0, 224, CW, 24);
            }
            else
            {
                string scroll = p + ".Lands";
                UScroll(c, p, scroll, 0, viewY, CW, viewH, list.Count * (rowH + 8f));
                for (int i = 0; i < list.Count; i++)
                    DrawTerritoryRow(c, scroll, player, clan, list[i], i, i * (rowH + 8f), CW - 12f, rowH);
            }

            string info = p + ".Rules";
            UPanel(c, p, info, ColPanelDark, 0, CH - infoH, CW, infoH, SpriteTile);
            UIcon(c, info, IcoFuel, 10, 6, 32);
            UText(c, info, string.Format("Захват: <b>{0}</b>   •   Расход: <b>{1} ТНК</b> каждые {2} сек   •   Для старта: <b>{3}</b> уч.   •   Награда: <b>+{4}</b> (+{5} за отбор)   •   Удержание: <b>+{6}</b> / {7}",
                FormatMinutes(_config.Capture.CaptureDurationSeconds), _config.Capture.FuelPerTick, _config.Capture.TickSeconds, _config.Capture.MinimumMembersToStart,
                _config.Score.CapturePoints, _config.Score.TakeoverBonusPoints, _config.Score.HoldPoints, FormatMinutes(_config.Score.HoldRewardIntervalSeconds)),
                11, ColTextDim, TextAnchor.MiddleLeft, 50, 0, CW - 60f, infoH);
        }

        private void DrawTerritoryRow(CuiElementContainer c, string parent, BasePlayer player, ClanData clan, RuntimeTerritory territory, int index, float y, float w, float h)
        {
            string row = parent + ".T" + index;
            TerritoryState st = territory.State;
            ulong userId = player.userID;
            long now = UnixNow();
            bool capturing = !string.IsNullOrEmpty(st.CapturingTag);
            bool ownCapture = capturing && string.Equals(st.CapturingTag, clan.Tag, StringComparison.OrdinalIgnoreCase);
            bool ownedByUs = string.Equals(st.OwnerTag, clan.Tag, StringComparison.OrdinalIgnoreCase);

            UPanel(c, parent, row, ColPanel, 0, y, w, h, SpriteTile);
            UPanel(c, row, null, capturing ? ColAccent : ownedByUs ? ColGreenBtn : ColBorder, 0, 0, 3, h);
            UPanel(c, row, null, ColPanelDark, 14, 14, 90, 90, SpriteTile);
            UIcon(c, row, TerritoryIcon(territory.Config), 24, 24, 70);

            UText(c, row, EscapeRichText(territory.Config.DisplayName.ToUpperInvariant()), 17, ColText, TextAnchor.MiddleLeft, 118, 10, 360, 26, true);
            string ownerLine;
            if (string.IsNullOrEmpty(st.OwnerTag))
                ownerLine = "ВЛАДЕЛЕЦ: <color=#9a948a>НЕЙТРАЛЬНАЯ</color>";
            else
            {
                ClanData owner = GetClanByTag(st.OwnerTag);
                ownerLine = string.Format("ВЛАДЕЛЕЦ: <color={0}>[{1}]</color> {2}{3}", TagColor, st.OwnerTag, owner == null ? string.Empty : EscapeRichText(Truncate(owner.DisplayName, 18)), ownedByUs ? "  <color=#9fd36b>(ВАШ КЛАН)</color>" : string.Empty);
            }
            UText(c, row, ownerLine, 12, ColTextDim, TextAnchor.MiddleLeft, 118, 36, 400, 20);

            float barW = w - 118f - 236f;
            if (capturing)
            {
                UText(c, row, string.Format("ЗАХВАТЫВАЕТ <color={0}>[{1}]</color>    {2}", TagColor, st.CapturingTag, GetCaptureStatusShort(territory)), 12, ColText, TextAnchor.MiddleLeft, 118, 60, barW, 18, true);
                DrawBar(c, row, 118, 82, barW, 12, CaptureRatio(st), ownCapture ? ColSegHold : ColAccent);
                bool hasFuel = EventGeneratorHasFuel(territory.Generator);
                int remaining = Mathf.Max(0, Mathf.CeilToInt(_config.Capture.CaptureDurationSeconds - st.ProgressSeconds));
                UText(c, row, string.Format("{0}%   •   осталось ~{1}   •   ТНК в генераторе: {2}", CapturePercent(st), FormatDuration(remaining), hasFuel ? "<color=#9fd36b>есть</color>" : "<color=#ff6b5e>нет</color>"), 11, ColTextDim, TextAnchor.MiddleLeft, 118, 96, barW, 16);
            }
            else if (st.CooldownUntilUnix > now)
            {
                UText(c, row, "ПЕРЕЗАРЯДКА: " + FormatDuration(st.CooldownUntilUnix - now), 13, ColOrangeTxt, TextAnchor.MiddleLeft, 118, 62, barW, 20, true);
                UText(c, row, "Новый захват станет доступен после окончания перезарядки.", 11, ColTextMute, TextAnchor.MiddleLeft, 118, 84, barW, 18);
            }
            else if (territory.Monument == null)
            {
                UText(c, row, "РТ НЕ НАЙДЕН НА ЭТОЙ КАРТЕ", 13, ColRedTimer, TextAnchor.MiddleLeft, 118, 62, barW, 20, true);
            }
            else if (!territory.Config.Calibrated)
            {
                UText(c, row, "ТОЧКА ЗАХВАТА ЕЩЁ НЕ НАСТРОЕНА", 13, ColRedTimer, TextAnchor.MiddleLeft, 118, 62, barW, 20, true);
                UText(c, row, "Администратор должен поставить точку генератора.", 11, ColTextMute, TextAnchor.MiddleLeft, 118, 84, barW, 18);
            }
            else
            {
                UText(c, row, "ДОСТУПНА ДЛЯ ЗАХВАТА", 13, ColGreenTxt, TextAnchor.MiddleLeft, 118, 62, barW, 20, true);
                UText(c, row, "Глава подходит к площадке из мешков с песком и нажимает «НАЧАТЬ ЗАХВАТ».", 11, ColTextMute, TextAnchor.MiddleLeft, 118, 84, barW, 18);
            }

            float distance = territory.Monument == null ? -1f : Vector3.Distance(player.transform.position, territory.CapturePoint);
            bool near = distance >= 0f && distance <= _config.Capture.StartDistance;
            bool leader = IsLeader(clan, userId);
            bool canStart = leader && territory.Config.Calibrated && territory.Monument != null && near && !capturing &&
                            st.CooldownUntilUnix <= now && !ownedByUs;
            bool activeEnemyCapture = capturing && !ownCapture;
            bool fuelEmpty = territory.Generator != null && !territory.Generator.IsDestroyed && !EventGeneratorHasFuel(territory.Generator);
            bool takeoverUnlocked = st.TakeoverLockedUntilUnix <= now;
            bool canTakeover = _config.Capture.AllowCaptureTakeover && CanTakeoverCapture(clan, userId) &&
                               territory.Config.Calibrated && near && activeEnemyCapture &&
                               (!_config.Capture.TakeoverRequiresEmptyFuel || fuelEmpty) && takeoverUnlocked;

            string text;
            string command = string.Empty;
            string color = ColDisabledBtn;
            string textColor = ColDisabledTxt;
            if (canStart)
            {
                text = "НАЧАТЬ ЗАХВАТ";
                command = "rrclan.ui capture";
                color = ColGreenBtn;
                textColor = ColGreenTxt;
            }
            else if (canTakeover)
            {
                text = "ПЕРЕХВАТИТЬ ЗАХВАТ";
                command = "rrclan.ui takeover " + territory.Config.Id;
                color = ColAccent;
                textColor = "1 0.92 0.86 1";
            }
            else if (ownCapture && leader)
            {
                text = "ОТКАЗАТЬСЯ ОТ ЗАХВАТА";
                command = "rrclan.ui cancelcapture";
                color = ColRedBtn;
                textColor = ColRedTxt;
            }
            else if (ownCapture)
                text = "ИДЁТ ВАШ ЗАХВАТ";
            else if (ownedByUs)
                text = "УЖЕ ВАША ТЕРРИТОРИЯ";
            else if (activeEnemyCapture && !_config.Capture.AllowCaptureTakeover)
                text = "ПЕРЕХВАТ ОТКЛЮЧЁН";
            else if (activeEnemyCapture && !CanTakeoverCapture(clan, userId))
                text = "ПЕРЕХВАТ: ГЛАВА / ЗАМ";
            else if (activeEnemyCapture && _config.Capture.TakeoverRequiresEmptyFuel && !fuelEmpty)
                text = "СНАЧАЛА ВЫНЬТЕ ТНК";
            else if (activeEnemyCapture && !takeoverUnlocked)
                text = "ЗАЩИТА: " + FormatDuration(st.TakeoverLockedUntilUnix - now);
            else if (!leader && !activeEnemyCapture)
                text = "НАЧИНАЕТ ТОЛЬКО ГЛАВА";
            else if (!territory.Config.Calibrated || territory.Monument == null)
                text = "НЕДОСТУПНО";
            else if (st.CooldownUntilUnix > now)
                text = "ПЕРЕЗАРЯДКА";
            else if (!near)
                text = "ПОДОЙДИТЕ К ТОЧКЕ";
            else
                text = "НЕДОСТУПНО";

            UButton(c, row, command, color, text, 12, textColor, w - 220f, 18, 206, 44);
            string distanceText = distance < 0f ? "РТ не найден" : near ? "<color=#9fd36b>ВЫ НА ТОЧКЕ</color>" : "ДО ПЛОЩАДКИ: " + Mathf.RoundToInt(distance) + " м";
            UText(c, row, distanceText, 11, ColTextDim, TextAnchor.MiddleCenter, w - 220f, 66, 206, 18, true);
            if (territory.Monument != null)
                UText(c, row, string.Format("Ваших на территории: {0}", CountClanMembersInside(clan.Tag, territory)), 11, ColTextMute, TextAnchor.MiddleCenter, w - 220f, 86, 206, 18);
        }

        private void DrawRankingTab(CuiElementContainer c, BasePlayer player, ClanData ownClan)
        {
            string p = ClanDynamic;
            List<ClanData> clans = GetLeaderboard();
            DrawTitle(c, p, "РЕЙТИНГ ВАЙПА", "Кланов: " + clans.Count + "  •  при равных очках выше тот, кто набрал их раньше");

            float y = 62f;
            if (!string.IsNullOrEmpty(_data.LastWinnerTag))
            {
                string banner = p + ".Winner";
                UPanel(c, p, banner, ColPanel, 0, y, CW, 38, SpriteTile);
                UPanel(c, banner, null, ColGold, 0, 0, 3, 38);
                UIcon(c, banner, IcoRank, 12, 5, 28);
                UText(c, banner, string.Format("ПОБЕДИТЕЛЬ ПРОШЛОГО ВАЙПА:  <color={0}>[{1}]</color>  —  {2} очк.", TagColor, EscapeRichText(_data.LastWinnerTag), _data.LastWinnerScore), 13, ColText, TextAnchor.MiddleLeft, 50, 0, CW - 60f, 38, true);
                y += 46f;
            }

            if (clans.Count == 0)
            {
                UIcon(c, p, IcoRank, CW / 2f - 32f, y + 90f, 64);
                UText(c, p, "Кланов пока нет. Станьте первым!", 14, ColTextDim, TextAnchor.MiddleCenter, 0, y + 164f, CW, 24);
                return;
            }

            float[] colX = { 0f, 60f, 430f, 530f, 620f, 700f };
            string headColor = ColTextMute;
            UText(c, p, "#", 10, headColor, TextAnchor.MiddleCenter, colX[0], y, 52, 20, true);
            UText(c, p, "КЛАН", 10, headColor, TextAnchor.MiddleLeft, colX[1], y, 360, 20, true);
            UText(c, p, "СОСТАВ", 10, headColor, TextAnchor.MiddleCenter, colX[2], y, 90, 20, true);
            UText(c, p, "ЗАХВАТЫ", 10, headColor, TextAnchor.MiddleCenter, colX[3], y, 80, 20, true);
            UText(c, p, "PVP", 10, headColor, TextAnchor.MiddleCenter, colX[4], y, 70, 20, true);
            UText(c, p, "ОЧКИ", 10, headColor, TextAnchor.MiddleRight, colX[5], y, 90, 20, true);
            y += 24f;

            // Показываем топ-50 и, если ваш клан ниже, — его строку в конце.
            List<int> shown = Enumerable.Range(0, Math.Min(50, clans.Count)).ToList();
            int ownIndex = ownClan == null ? -1 : clans.FindIndex(x => x.ClanId == ownClan.ClanId);
            if (ownIndex >= 50)
                shown.Add(ownIndex);

            float rowH = 46f;
            string scroll = p + ".Rank";
            UScroll(c, p, scroll, 0, y, CW, CH - y, shown.Count * (rowH + 4f));
            float rowW = CW - 12f;
            for (int k = 0; k < shown.Count; k++)
            {
                int i = shown[k];
                ClanData entry = clans[i];
                bool own = ownClan != null && entry.ClanId == ownClan.ClanId;
                string row = scroll + ".R" + i;
                UPanel(c, scroll, row, own ? ColAccentSoft : (k % 2 == 0 ? ColRowA : ColRowB), 0, k * (rowH + 4f), rowW, rowH, SpriteTile);
                if (own)
                    UPanel(c, row, null, ColAccent, 0, 0, 3, rowH);

                int place = i + 1;
                if (place <= 3)
                {
                    UPanel(c, row, null, RankColor(place), 12, 7, 32, 32, SpriteCircle);
                    UText(c, row, place.ToString(), 14, "0.1 0.1 0.1 1", TextAnchor.MiddleCenter, 12, 7, 32, 32, true);
                }
                else
                    UText(c, row, place.ToString(), 14, ColTextDim, TextAnchor.MiddleCenter, 12, 0, 32, rowH, true);

                UText(c, row, string.Format("<color={0}>[{1}]</color>  {2}", TagColor, entry.Tag, EscapeRichText(Truncate(entry.DisplayName, 26))), 14, ColText, TextAnchor.MiddleLeft, colX[1], 0, 360, rowH, true);
                UText(c, row, entry.Members.Count + "/" + _config.Clan.MaxMembers, 12, ColTextDim, TextAnchor.MiddleCenter, colX[2], 0, 90, rowH);
                UText(c, row, entry.Captures.ToString(), 12, ColTextDim, TextAnchor.MiddleCenter, colX[3], 0, 80, rowH);
                UText(c, row, entry.Kills.ToString(), 12, ColTextDim, TextAnchor.MiddleCenter, colX[4], 0, 70, rowH);
                UText(c, row, entry.Score.ToString(), 15, place <= 3 ? RankColor(place) : ColText, TextAnchor.MiddleRight, colX[5] - 8f, 0, 90, rowH, true);
            }
        }

        private void DrawHelpTab(CuiElementContainer c, BasePlayer player)
        {
            string p = ClanDynamic;
            DrawTitle(c, p, "СПРАВКА", "Как устроены кланы на сервере");
            string selected;
            if (!_helpSections.TryGetValue(player.userID, out selected))
                selected = "basics";

            string[] ids = { "basics", "roles", "points", "capture", "commands" };
            string[] titles = { "КАК НАЧАТЬ", "РОЛИ", "ОЧКИ", "ЗАХВАТ РТ", "КОМАНДЫ" };
            float bw = (CW - 4f * 8f) / ids.Length;
            for (int i = 0; i < ids.Length; i++)
            {
                bool active = selected == ids[i];
                string name = p + ".HelpTab" + i;
                UButton(c, p, "rrclan.help " + ids[i], active ? ColSlotSolid : ColPanelDark, titles[i], 12, active ? ColText : ColTextDim, i * (bw + 8f), 62, bw, 34, name);
                if (active)
                    UPanel(c, name, null, ColAccent, 0, 31, bw, 3);
            }

            string body = GetHelpSectionBody(selected);
            float viewY = 108f;
            float viewH = CH - viewY;
            int lines = body.Split('\n').Length + body.Length / 95;
            float contentH = Mathf.Max(viewH, 40f + lines * 19f);
            string box = p + ".HelpBox";
            UPanel(c, p, box, ColPanel, 0, viewY, CW, viewH, SpriteTile);
            string scroll = box + ".Scroll";
            UScroll(c, box, scroll, 0, 0, CW, viewH, contentH);
            UPanel(c, scroll, null, ColAccent, 16, 18, 3, contentH - 36f);
            UText(c, scroll, body, 13, ColText, TextAnchor.UpperLeft, 32, 14, CW - 64f, contentH - 28f);
        }

        private void DrawGreenTeamTab(CuiElementContainer c, BasePlayer player, ClanData clan)
        {
            string p = ClanDynamic;
            ulong userId = player.userID;
            bool leader = IsLeader(clan, userId);
            DrawTitle(c, p, "ЗЕЛЁНАЯ КОМАНДА", "Привязанная команда Rust: общий шкаф, карта, метки и голос");

            RelationshipManager.PlayerTeam team = GetManualGreenTeam(clan);
            if (team == null)
            {
                string empty = p + ".Empty";
                UPanel(c, p, empty, ColPanel, 0, 62, CW, 200, SpriteTile);
                UPanel(c, empty, null, ColAccent, 0, 0, 3, 200);
                UIcon(c, empty, IcoGreen, 24, 40, 96);
                UText(c, empty, "ЗЕЛЁНКА НЕ ПРИВЯЗАНА", 18, ColText, TextAnchor.MiddleLeft, 140, 30, 500, 28, true);
                UText(c, empty, "1. Создайте обычную команду через меню Rust (глава должен быть её лидером).\n2. Нажмите кнопку ниже — плагин запомнит именно эту команду.\n3. Затем приглашайте в неё участников клана отсюда.", 12, ColTextDim, TextAnchor.UpperLeft, 140, 64, CW - 160f, 70);
                if (leader)
                    UButton(c, empty, "rrclan.green bind", ColGreenBtn, "ПРИВЯЗАТЬ МОЮ ТЕКУЩУЮ КОМАНДУ", 12, ColGreenTxt, 140, 142, 300, 40);
                else
                    UText(c, empty, "Привязать команду может только глава клана.", 12, ColTextMute, TextAnchor.MiddleLeft, 140, 142, 400, 40);
                return;
            }

            bool valid = team.teamLeader == clan.OwnerId && team.members.Contains(clan.OwnerId);
            string head = p + ".Head";
            UPanel(c, p, head, ColPanel, 0, 62, CW, 76, SpriteTile);
            UPanel(c, head, null, valid ? ColGreenBtn : ColRedBtn, 0, 0, 3, 76);
            UIcon(c, head, IcoGreen, 14, 14, 48);
            UText(c, head, "КОМАНДА №" + team.teamID, 16, ColText, TextAnchor.MiddleLeft, 74, 8, 400, 24, true);
            string leaderName = GetKnownName(clan, team.teamLeader);
            UText(c, head, string.Format("Лидер команды: <color=#e8ded3>{0}</color>    •    Состав: <color=#e8ded3>{1}/8</color>", EscapeRichText(Truncate(leaderName, 20)), team.members.Count), 12, ColTextDim, TextAnchor.MiddleLeft, 74, 32, 460, 18);
            UText(c, head, valid ? "<color=#9fd36b>Всё в порядке: глава клана — лидер команды.</color>" : "<color=#ff6b5e>Глава клана не лидер команды — нажмите ВЕРНУТЬ ЛИДЕРСТВО.</color>", 11, ColText, TextAnchor.MiddleLeft, 74, 50, 460, 18);
            if (leader)
            {
                UButton(c, head, "rrclan.green restoreleader", ColBlueBtn, "ВЕРНУТЬ ЛИДЕРСТВО ГЛАВЕ", 11, ColBlueTxt, CW - 226f, 8, 212, 28);
                bool confirmUnbind = IsConfirming(player, "unbind");
                UButton(c, head, "rrclan.green unbind", confirmUnbind ? ColRedHot : ColRedBtn, confirmUnbind ? "ТОЧНО ОТВЯЗАТЬ?" : "ОТВЯЗАТЬ КОМАНДУ", 11, ColRedTxt, CW - 226f, 40, 212, 28);
            }

            List<ulong> members = clan.Members.OrderBy(id => GetRoleSort(clan, id)).ThenBy(id => GetKnownName(clan, id)).ToList();
            float rowH = 54f;
            float viewY = 150f;
            float viewH = CH - viewY - 26f;
            string scroll = p + ".Green";
            UScroll(c, p, scroll, 0, viewY, CW, viewH, members.Count * (rowH + 4f));
            float rowW = CW - 12f;
            for (int i = 0; i < members.Count; i++)
            {
                ulong memberId = members[i];
                bool inside = team.members.Contains(memberId);
                string row = scroll + ".G" + i;
                UPanel(c, scroll, row, i % 2 == 0 ? ColRowA : ColRowB, 0, i * (rowH + 4f), rowW, rowH, SpriteTile);
                UPanel(c, row, null, inside ? ColGreenBtn : ColBorder, 0, 0, 3, rowH);
                UAvatar(c, row, memberId, 10, 7, 40);
                UPanel(c, row, null, IsOnline(memberId) ? ColGreenDot : ColGrayDot, 42, 37, 10, 10, SpriteCircle);
                UText(c, row, EscapeRichText(Truncate(GetKnownName(clan, memberId), 22)), 13, ColText, TextAnchor.MiddleLeft, 62, 6, 300, 22, true);
                UText(c, row, GetRoleName(clan, memberId), 11, ColTextDim, TextAnchor.MiddleLeft, 62, 28, 300, 18);
                DrawChip(c, row, inside ? "В КОМАНДЕ" : "НЕ В КОМАНДЕ", inside ? "0.25 0.32 0.15 1" : ColSlotSolid, inside ? ColGreenTxt : ColTextDim, rowW - 360f, 17, 110, 20);

                if (memberId == clan.OwnerId)
                    UText(c, row, inside ? "ГЛАВА" : "ГЛАВА ВНЕ КОМАНДЫ!", 12, inside ? ColGold : ColRedTimer, TextAnchor.MiddleCenter, rowW - 230f, 0, 216, rowH, true);
                else if (inside)
                    UButton(c, row, "rrclan.green remove " + memberId, ColRedBtn, "УБРАТЬ ИЗ КОМАНДЫ", 11, ColRedTxt, rowW - 230f, 11, 216, 32);
                else
                    UButton(c, row, "rrclan.green invite " + memberId, ColGreenBtn, IsOnline(memberId) ? "ПРИГЛАСИТЬ В КОМАНДУ" : "ОТПРАВИТЬ ПРИГЛАШЕНИЕ", 11, ColGreenTxt, rowW - 230f, 11, 216, 32);
            }

            UText(c, p, "Приглашение приходит в обычное меню команды Rust — игрок должен принять его сам. Управлять командой могут глава и заместитель.", 11, ColTextMute, TextAnchor.MiddleLeft, 0, CH - 22f, CW, 20);
        }

        private void DrawManagementTab(CuiElementContainer c, BasePlayer player, ClanData clan)
        {
            string p = ClanDynamic;
            DrawTitle(c, p, "УПРАВЛЕНИЕ", "Настройки клана — доступно только главе");

            float half = (CW - 12f) / 2f;
            DrawEditCard(c, p, "name", "НАЗВАНИЕ КЛАНА", EscapeRichText(clan.DisplayName), clan.NameChangedThisWipe, "rrclan.manage rename", 32, "2–32 символа: буквы, цифры, пробел и - _ .", 0, 62, half, 136);
            DrawEditCard(c, p, "tag", "ТЕГ КЛАНА", string.Format("<color={0}>[{1}]</color>", TagColor, clan.Tag), clan.TagChangedThisWipe, "rrclan.manage retag", Math.Max(2, _config.Clan.MaxTagLength), string.Format("{0}–{1} символов: A–Z и 0–9", _config.Clan.MinTagLength, _config.Clan.MaxTagLength), half + 12f, 62, half, 136);

            float y = 210f;
            string cap = p + ".Capture";
            float capH = 96f;
            UPanel(c, p, cap, ColPanel, 0, y, CW, capH, SpriteTile);
            UIcon(c, cap, IcoCapture, 16, 22, 52);
            RuntimeTerritory active = _runtimeTerritories.Values.FirstOrDefault(t => string.Equals(t.State.CapturingTag, clan.Tag, StringComparison.OrdinalIgnoreCase));
            if (active != null)
            {
                UPanel(c, cap, null, ColAccent, 0, 0, 3, capH);
                UText(c, cap, "ИДЁТ ЗАХВАТ: " + EscapeRichText(active.Config.DisplayName.ToUpperInvariant()), 14, ColText, TextAnchor.MiddleLeft, 84, 12, 460, 22, true);
                UText(c, cap, GetCaptureStatusShort(active) + "   •   " + CapturePercent(active.State) + "%", 12, ColText, TextAnchor.MiddleLeft, 84, 36, 460, 18);
                DrawBar(c, cap, 84, 62, CW - 84f - 260f, 12, CaptureRatio(active.State), ColAccent);
                UButton(c, cap, "rrclan.manage cancelcapture", ColRedBtn, "ОТКАЗАТЬСЯ ОТ ЗАХВАТА", 12, ColRedTxt, CW - 236f, 26, 220, 44);
            }
            else
            {
                UText(c, cap, "АКТИВНОГО ЗАХВАТА НЕТ", 14, ColText, TextAnchor.MiddleLeft, 84, 18, 460, 22, true);
                UText(c, cap, "Чтобы начать захват, подойдите к площадке на РТ и нажмите кнопку во вкладке ТЕРРИТОРИИ.", 12, ColTextDim, TextAnchor.MiddleLeft, 84, 44, 480, 36);
                UButton(c, cap, "rrclan.ui tab territory", ColBlueBtn, "ОТКРЫТЬ ТЕРРИТОРИИ", 12, ColBlueTxt, CW - 236f, 26, 220, 44);
            }

            y += capH + 12f;
            string lead = p + ".Lead";
            float leadH = 86f;
            UPanel(c, p, lead, ColPanel, 0, y, CW, leadH, SpriteTile);
            UIcon(c, lead, IcoMembers, 16, 17, 52);
            string deputy = clan.DeputyId == 0UL ? "<color=#9a948a>не назначен</color>" : "<color=#bcdcff>" + EscapeRichText(GetKnownName(clan, clan.DeputyId)) + "</color>";
            UText(c, lead, "ЗАМЕСТИТЕЛЬ: " + deputy, 14, ColText, TextAnchor.MiddleLeft, 84, 14, 480, 22, true);
            UText(c, lead, "Роли, заместитель, передача главы и исключение — прямо в списке во вкладке СОСТАВ.", 12, ColTextDim, TextAnchor.MiddleLeft, 84, 40, 480, 34);
            UButton(c, lead, "rrclan.ui tab members", ColGrayBtn, "ОТКРЫТЬ СОСТАВ", 12, ColText, CW - 236f, 21, 220, 44);

            string danger = p + ".Danger";
            float dangerH = 76f;
            float dangerY = CH - dangerH;
            bool confirming = IsConfirming(player, "disband");
            UPanel(c, p, danger, "0.200 0.090 0.070 0.95", 0, dangerY, CW, dangerH, SpriteTile);
            UPanel(c, danger, null, ColRedBtn, 0, 0, 3, dangerH);
            UText(c, danger, "РОСПУСК КЛАНА", 14, ColRedTxt, TextAnchor.MiddleLeft, 18, 12, 400, 22, true);
            UText(c, danger, "Клан, его очки и территории будут удалены безвозвратно. Нужно нажать кнопку дважды.", 12, "0.85 0.65 0.60 1", TextAnchor.MiddleLeft, 18, 36, 520, 30);
            UButton(c, danger, "rrclan.manage disband", confirming ? ColRedHot : ColRedBtn, confirming ? "ПОДТВЕРДИТЬ РОСПУСК" : "РАСПУСТИТЬ КЛАН", 12, ColRedTxt, CW - 236f, 16, 220, 44);
        }

        private void DrawEditCard(CuiElementContainer c, string parent, string key, string title, string current, bool locked, string command, int limit, string rule, float x, float y, float w, float h)
        {
            string card = parent + ".Edit." + key;
            UPanel(c, parent, card, ColPanel, x, y, w, h, SpriteTile);
            UText(c, card, title, 11, ColTextDim, TextAnchor.MiddleLeft, 16, 10, w - 32f, 16, true);
            UText(c, card, current, 20, ColText, TextAnchor.MiddleLeft, 16, 28, w - 32f, 30, true);
            if (locked)
            {
                DrawChip(c, card, "УЖЕ ИЗМЕНЕНО НА ЭТОМ ВАЙПЕ", ColSlotSolid, ColTextDim, 16, 70, 220, 22);
                UText(c, card, "Следующая смена станет доступна после вайпа карты.", 11, ColTextMute, TextAnchor.MiddleLeft, 16, 100, w - 32f, 20);
                return;
            }

            DrawInput(c, card, card + ".Input", command, limit, 16, 66, w - 32f, 36);
            UText(c, card, rule + "  •  Enter — сохранить  •  1 раз за вайп", 10, ColTextMute, TextAnchor.MiddleLeft, 16, 108, w - 32f, 18);
        }

        private string GetHelpSectionBody(string section)
        {
            if (section == "roles")
            {
                string roles = string.Join(", ", _config.Clan.MemberRoles.ToArray());
                return "<size=20><b>РОЛИ В КЛАНЕ</b></size>\n\n" +
                       "<color=#efc34d><b>ГЛАВА</b></color>\nСоздаёт клан, меняет название и тег, назначает заместителя, передаёт главу, исключает участников и начинает захваты.\n\n" +
                       "<color=#bcdcff><b>ЗАМЕСТИТЕЛЬ</b></color>\nНазначается главой из игроков, которые уже состоят с ним в одной зелёной команде Rust. Управляет приглашениями в зелёнку и может перехватывать чужие захваты.\n\n" +
                       "<b>НАЗНАЧЕНИЯ УЧАСТНИКОВ</b>\nГлава может поставить подпись: <color=#d6a84b>" + EscapeRichText(roles) + "</color>. Это только обозначение задачи игрока, дополнительных прав оно не даёт.";
            }

            if (section == "points")
            {
                return string.Format(
                    "<size=20><b>КАК НАЧИСЛЯЮТСЯ ОЧКИ</b></size>\n\n" +
                    "<color=#9fd36b><b>+{0}</b></color> — убийство участника другого клана. Убийство игрока без клана очков не даёт.\n" +
                    "<color=#9fd36b><b>+{1}</b></color> — захват нейтральной территории.\n" +
                    "<color=#9fd36b><b>+{2}</b></color> дополнительно — если территория отобрана у другого клана.\n" +
                    "<color=#9fd36b><b>+{3}</b></color> — удержание территории каждые <b>{4}</b>, пока на ней есть участники клана.\n\n" +
                    "Повторное убийство той же жертвы тем же игроком даст очки только через <b>{5}</b>. Других противников можно убивать без ожидания.\n\n" +
                    "Во вкладке ОБЗОР видно, сколько очков клан получил за PvP, захваты и удержание отдельно. Побеждает клан, у которого к концу вайпа больше всего очков.",
                    _config.Score.KillPoints, _config.Score.CapturePoints, _config.Score.TakeoverBonusPoints, _config.Score.HoldPoints,
                    FormatMinutes(_config.Score.HoldRewardIntervalSeconds), FormatMinutes(_config.Score.SameVictimCooldownSeconds));
            }

            if (section == "capture")
            {
                return string.Format(
                    "<size=20><b>ЗАХВАТ ТЕРРИТОРИИ</b></size>\n\n" +
                    "<b>1. Старт.</b> На каждом РТ стоит площадка из мешков с песком. Глава подходит к ней не дальше чем на <b>{0:0} м</b>, рядом должно быть минимум <b>{4}</b> участников клана. Во вкладке ТЕРРИТОРИИ нажмите <b>НАЧАТЬ ЗАХВАТ</b> — в центре площадки появится генератор.\n\n" +
                    "<b>2. Топливо.</b> Положите в генератор <b>ТНК</b>. Каждые <b>{1} сек</b> сгорает <b>{2}</b> ТНК и идёт прогресс. Всего захват длится <b>{3}</b> работы генератора.\n\n" +
                    "<b>3. Защита.</b> Если на территории есть игроки другого клана, прогресс стоит. Если на точке не осталось ваших — прогресс медленно падает.\n\n" +
                    "<b>4. Перехват.</b> Чужой клан полностью вынимает ТНК из генератора, затем глава или заместитель нажимает <b>ПЕРЕХВАТИТЬ ЗАХВАТ</b>. Прогресс сбрасывается, новый клан кладёт своё ТНК. После перехвата есть короткая защита от мгновенного обратного перехвата.\n\n" +
                    "<b>5. Итог.</b> На 100% территория переходит клану, и он начинает получать очки за удержание. После захвата у РТ перезарядка <b>{5}</b>.",
                    _config.Capture.StartDistance, _config.Capture.TickSeconds, _config.Capture.FuelPerTick,
                    FormatMinutes(_config.Capture.CaptureDurationSeconds), _config.Capture.MinimumMembersToStart, FormatMinutes(_config.Capture.TerritoryCooldownSeconds));
            }

            if (section == "commands")
            {
                return "<size=20><b>КОМАНДЫ</b></size>\n\n" +
                       "Почти всё делается кнопками в меню, но команды тоже работают:\n\n" +
                       "<b>/clan</b> — открыть меню.\n" +
                       "<b>/clan create TAG</b> — создать клан.\n" +
                       "<b>/clan invite ник</b> — пригласить игрока.\n" +
                       "<b>/clan accept</b> / <b>/clan decline</b> — принять или отклонить приглашение.\n" +
                       "<b>/clan leave</b> — выйти из клана.\n" +
                       "<b>/clan top</b> — рейтинг в чат.\n" +
                       "<b>/clan capture</b> — начать захват рядом с точкой.\n" +
                       "<b>/clan cancelcapture</b> — отказаться от своего захвата.\n" +
                       "<b>/clan takeover</b> — перехватить чужой захват рядом.\n" +
                       "<b>/c текст</b> — клановый чат.\n\n" +
                       "<b>ДЛЯ ГЛАВЫ</b>\n" +
                       "<b>/clan deputy ник</b> — назначить или снять заместителя.\n" +
                       "<b>/clan kick ник</b> — исключить игрока.\n" +
                       "<b>/clan transfer ник</b> — передать главу.\n" +
                       "<b>/clan rename название</b> и <b>/clan retag TAG</b> — по одному разу за вайп.";
            }

            return "<size=20><b>КАК НАЧАТЬ</b></size>\n\n" +
                   "<b>1.</b> Создайте обычную зелёную команду через меню Rust. В ней должен быть только будущий глава.\n\n" +
                   "<b>2.</b> Откройте <color=#d6a84b><b>/clan</b></color>, впишите тег на главной вкладке и нажмите Enter. Плагин привяжет именно вашу команду.\n\n" +
                   "<b>3.</b> Во вкладке СОСТАВ пригласите игроков в клан. Затем во вкладке ЗЕЛЁНКА отправьте им приглашение в команду — игрок примет его в обычном меню Rust.\n\n" +
                   "<b>4.</b> Набирайте очки: убивайте игроков других кланов, захватывайте и удерживайте РТ. Рейтинг обнуляется каждый вайп.\n\n" +
                   "Название и тег можно изменить по одному разу за вайп. PvP засчитывается только между участниками разных кланов.";
        }

        private void DrawClanInfoPopup(CuiElementContainer c, ClanData clan, string section)
        {
            string title;
            string body = GetClanInfoBody(clan, section, out title);
            string shade = ClanDynamic + ".InfoShade";
            UButton(c, ClanDynamic, "rrclan.info close", "0 0 0 0.6", string.Empty, 1, ColText, 0, 0, CW, CH, shade);
            float w = 480f, h = 230f;
            string popup = ClanDynamic + ".Info";
            UPanel(c, ClanDynamic, popup, ColWindow, (CW - w) / 2f, (CH - h) / 2f, w, h, SpriteTile);
            UFrame(c, popup, ColBorder, 0, 0, w, h);
            UPanel(c, popup, null, ColAccent, 0, 0, 3, h);
            UText(c, popup, title, 15, ColText, TextAnchor.MiddleLeft, 20, 12, w - 80f, 24, true);
            UCloseButton(c, popup, "rrclan.info close", w - 44f, 10, 32);
            UPanel(c, popup, null, ColBorder, 20, 48, w - 40f, 1);
            UText(c, popup, body, 12, ColTextDim, TextAnchor.UpperLeft, 20, 58, w - 40f, h - 70f);
        }

        private string GetClanInfoBody(ClanData clan, string section, out string title)
        {
            if (section == "rank")
            {
                title = "МЕСТО В РЕЙТИНГЕ";
                return "Место зависит от общего количества очков за текущий вайп. Чем больше очков, тем выше клан.\n\nЕсли у двух кланов одинаковые очки, выше тот, кто набрал эту сумму раньше. Затем сравниваются захваты и PvP.";
            }

            if (section == "captures")
            {
                title = "ЗАХВАТЫ";
                return string.Format("Захват засчитывается, когда прогресс генератора дошёл до 100%.\n\nНейтральная территория даёт <b>{0}</b> очков. За отбор территории у другого клана — ещё <b>{1}</b>.\n\nПодробности: СПРАВКА → ЗАХВАТ РТ.", _config.Score.CapturePoints, _config.Score.TakeoverBonusPoints);
            }

            if (section == "pvp")
            {
                title = "PVP";
                return string.Format("Количество засчитанных убийств игроков из других кланов. Каждое даёт клану <b>{0}</b> очк.\n\nПовторное убийство той же жертвы раньше чем через <b>{1}</b> очков не даст. Других противников можно убивать без ожидания.", _config.Score.KillPoints, FormatMinutes(_config.Score.SameVictimCooldownSeconds));
            }

            title = "ОЧКИ КЛАНА";
            return string.Format("Общий результат клана за текущий вайп.\n\n<b>+{0}</b> — убийство игрока другого клана.\n<b>+{1}</b> — захват нейтральной территории.\n<b>+{2}</b> — бонус за отбор территории.\n<b>+{3}</b> — удержание каждые {4}.\n\nПобеждает клан с наибольшим счётом к концу вайпа.", _config.Score.KillPoints, _config.Score.CapturePoints, _config.Score.TakeoverBonusPoints, _config.Score.HoldPoints, FormatMinutes(_config.Score.HoldRewardIntervalSeconds));
        }

        #endregion

        #region GUI: админ-панель

        private string GetAdminMode(BasePlayer player)
        {
            string mode;
            if (player == null || !_adminUiModes.TryGetValue(player.userID, out mode) || string.IsNullOrEmpty(mode))
                return "clans";
            return mode;
        }

        private string NormalizeAdminMode(string mode)
        {
            mode = string.IsNullOrEmpty(mode) ? "clans" : mode.ToLowerInvariant();
            switch (mode)
            {
                case "clans":
                case "green":
                case "territories":
                case "stats":
                case "settings":
                case "help":
                    return mode;
                default:
                    return "clans";
            }
        }

        private ClanData ResolveAdminSelectedClan(BasePlayer player, string selectedTag)
        {
            ulong userId = player.userID;
            if (string.IsNullOrEmpty(selectedTag))
            {
                string saved;
                if (_adminSelectedClans.TryGetValue(userId, out saved))
                    selectedTag = saved;
            }

            ClanData selected = GetClanByTag(selectedTag);
            if (selected == null)
                selected = GetLeaderboard().FirstOrDefault();
            if (selected != null)
                _adminSelectedClans[userId] = selected.Tag;
            else
                _adminSelectedClans.Remove(userId);
            return selected;
        }

        private void OpenAdminUi(BasePlayer player, string selectedTag, string mode)
        {
            if (player == null || !player.IsConnected || !HasAdminPermission(player))
                return;
            ulong userId = player.userID;

            if (_clanUiOpen.Contains(userId))
                CloseClanUi(player);

            mode = NormalizeAdminMode(mode);
            _adminUiModes[userId] = mode;
            ClanData selected = ResolveAdminSelectedClan(player, selectedTag);

            CuiElementContainer ui = new CuiElementContainer();
            if (!_adminUiOpen.Contains(userId))
            {
                CuiHelper.DestroyUi(player, AdminUiRoot);
                BuildWindowShell(ui, AdminUiRoot, AdminWindow);
                _adminUiOpen.Add(userId);
            }
            else
            {
                CuiHelper.DestroyUi(player, AdminHeaderDyn);
                CuiHelper.DestroyUi(player, AdminNav);
                CuiHelper.DestroyUi(player, AdminDynamic);
            }

            DrawAdminHeader(ui, player);
            DrawAdminNav(ui, player, mode);
            UPanel(ui, AdminWindow, AdminDynamic, "0 0 0 0", ContentX, ContentY, CW, CH);
            DrawAdminMode(ui, player, mode, selected);
            CuiHelper.AddUi(player, ui);
        }

        private void RefreshAdminContent(BasePlayer player)
        {
            if (player == null || !player.IsConnected || !HasAdminPermission(player))
                return;
            ulong userId = player.userID;
            if (!_adminUiOpen.Contains(userId))
                return;

            string mode = GetAdminMode(player);
            ClanData selected = ResolveAdminSelectedClan(player, string.Empty);
            CuiHelper.DestroyUi(player, AdminDynamic);
            CuiElementContainer ui = new CuiElementContainer();
            UPanel(ui, AdminWindow, AdminDynamic, "0 0 0 0", ContentX, ContentY, CW, CH);
            DrawAdminMode(ui, player, mode, selected);
            CuiHelper.AddUi(player, ui);
        }

        private void DrawAdminMode(CuiElementContainer c, BasePlayer player, string mode, ClanData selected)
        {
            switch (mode)
            {
                case "green":
                    DrawAdminClanList(c, player, selected, "green");
                    DrawAdminGreenDetail(c, player, selected);
                    break;
                case "territories":
                    DrawAdminTerritories(c, player, selected);
                    break;
                case "stats":
                    DrawAdminStatistics(c, player);
                    break;
                case "settings":
                    DrawAdminSettings(c, player);
                    break;
                case "help":
                    DrawAdminHelp(c);
                    break;
                default:
                    DrawAdminClanList(c, player, selected, "clans");
                    DrawAdminClanDetail(c, player, selected);
                    break;
            }
        }

        private void DrawAdminHeader(CuiElementContainer c, BasePlayer player)
        {
            UPanel(c, AdminWindow, AdminHeaderDyn, "0 0 0 0", 0, 0, WinW, HeaderH);
            string p = AdminHeaderDyn;
            UIcon(c, p, IcoAdmin, 18, 10, 36);
            UText(c, p, "АДМИН-ПАНЕЛЬ КЛАНОВ", 20, ColText, TextAnchor.MiddleLeft, 64, 5, 460, 26, true);
            int active = _runtimeTerritories.Values.Count(t => !string.IsNullOrEmpty(t.State.CapturingTag));
            string access = IsPublicSystemEnabled ? "<color=#9fd36b>ОТКРЫТА ДЛЯ ВСЕХ</color>" : "<color=#ffb35a>ТОЛЬКО АДМИНИСТРАТОРЫ</color>";
            UText(c, p, string.Format("Система: {0}   •   Кланов: {1}   •   Активных захватов: {2}", access, _data.Clans.Count, active), 12, ColTextDim, TextAnchor.MiddleLeft, 64, 30, 640, 20);

            float x = WinW - 12f - 40f;
            UCloseButton(c, p, "rrclan.adminui close", x, 8f);
            if (CanAccessSystem(player))
                UButton(c, p, "rrclan.adminui playerui", ColGrayBtn, "МЕНЮ ИГРОКА", 12, ColText, x - 8f - 130f, 8f, 130f, 40f);
        }

        private void DrawAdminNav(CuiElementContainer c, BasePlayer player, string active)
        {
            float navH = WinH - HeaderH - 1f;
            UPanel(c, AdminWindow, AdminNav, "0 0 0 0", 0, HeaderH + 1f, SideW, navH);

            List<NavTab> tabs = new List<NavTab>
            {
                new NavTab("clans", "КЛАНЫ", IcoMembers),
                new NavTab("green", "ЗЕЛЁНКА", IcoGreen),
                new NavTab("territories", "ТЕРРИТОРИИ", IcoCapture, _runtimeTerritories.Values.Any(t => !string.IsNullOrEmpty(t.State.CapturingTag))),
                new NavTab("stats", "СТАТИСТИКА", IcoStats),
                new NavTab("settings", "НАСТРОЙКИ", IcoSettings, !IsPublicSystemEnabled),
                new NavTab("help", "СПРАВКА", IcoHelp)
            };

            float y = 12f;
            foreach (NavTab tab in tabs)
            {
                DrawNavButton(c, AdminNav, "rrclan.adminui " + tab.Key, tab, tab.Key == active, y);
                y += 46f;
            }

            float cardY = navH - 72f;
            UPanel(c, AdminNav, null, ColBorder, 10, cardY - 10f, SideW - 20f, 1);
            UAvatar(c, AdminNav, player.userID, 12, cardY, 52);
            UText(c, AdminNav, EscapeRichText(Truncate(player.displayName, 16)), 13, ColText, TextAnchor.MiddleLeft, 74, cardY + 4f, SideW - 84f, 22, true);
            UText(c, AdminNav, "Администратор", 11, ColAccentTxt, TextAnchor.MiddleLeft, 74, cardY + 26f, SideW - 84f, 20);
        }

        private List<ClanData> FilterClans(List<ClanData> clans, string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return clans;
            string q = query.Trim();
            return clans.Where(clan =>
                    clan.Tag.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (!string.IsNullOrEmpty(clan.DisplayName) && clan.DisplayName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    clan.Members.Any(id => GetKnownName(clan, id).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || id.ToString() == q))
                .ToList();
        }

        private void DrawAdminClanList(CuiElementContainer c, BasePlayer player, ClanData selected, string mode)
        {
            string p = AdminDynamic;
            ulong userId = player.userID;
            float w = 250f;
            string box = p + ".List";
            UPanel(c, p, box, ColPanel, 0, 0, w, CH, SpriteTile);
            UText(c, box, "КЛАНЫ", 13, ColText, TextAnchor.MiddleLeft, 12, 8, 120, 20, true);

            string search;
            _adminSearch.TryGetValue(userId, out search);
            bool filtered = !string.IsNullOrEmpty(search);
            UText(c, box, filtered ? "Фильтр: <color=#e8ded3>" + EscapeRichText(Truncate(search, 18)) + "</color>" : "Тег, название или ник + Enter", 10, ColTextDim, TextAnchor.MiddleLeft, 12, 28, w - 24f, 16);
            DrawInput(c, box, box + ".Search", "rrclan.adminui search", 24, 12, 46, w - 24f - (filtered ? 36f : 0f), 30);
            if (filtered)
                UCloseButton(c, box, "rrclan.adminui clearsearch", w - 12f - 30f, 46, 30);

            List<ClanData> all = GetLeaderboard();
            List<ClanData> clans = FilterClans(all, search);
            if (clans.Count == 0)
            {
                UText(c, box, all.Count == 0 ? "Кланов пока нет." : "Ничего не найдено.", 12, ColTextMute, TextAnchor.UpperCenter, 12, 100, w - 24f, 40);
                return;
            }

            const int pageSize = 20;
            int pages = Math.Max(1, (clans.Count + pageSize - 1) / pageSize);
            int page;
            _adminPages.TryGetValue(userId, out page);
            if (selected != null && !_adminPages.ContainsKey(userId))
            {
                int selectedIndex = clans.FindIndex(x => x.ClanId == selected.ClanId);
                if (selectedIndex >= 0) page = selectedIndex / pageSize;
            }
            page = Mathf.Clamp(page, 0, pages - 1);
            _adminPages[userId] = page;
            List<ClanData> pageClans = clans.Skip(page * pageSize).Take(pageSize).ToList();

            float pagerH = pages > 1 ? 36f : 0f;
            string scroll = box + ".Scroll";
            float rowH = 50f;
            UScroll(c, box, scroll, 6, 86, w - 12f, CH - 92f - pagerH, pageClans.Count * (rowH + 4f));
            float rowW = w - 24f;
            string viewCommand = mode == "green" ? "rrclan.adminui greenview " : "rrclan.adminui view ";
            if (pages > 1)
            {
                float py = CH - 6f - 30f;
                UButton(c, box, page > 0 ? "rrclan.adminui listpage " + (page - 1) : string.Empty, page > 0 ? ColGrayBtn : ColDisabledBtn, "‹", 16, page > 0 ? ColText : ColDisabledTxt, 12, py, 40, 30);
                UText(c, box, string.Format("{0} / {1}", page + 1, pages), 12, ColTextDim, TextAnchor.MiddleCenter, 56, py, w - 112f, 30, true);
                UButton(c, box, page < pages - 1 ? "rrclan.adminui listpage " + (page + 1) : string.Empty, page < pages - 1 ? ColGrayBtn : ColDisabledBtn, "›", 16, page < pages - 1 ? ColText : ColDisabledTxt, w - 52f, py, 40, 30);
            }
            for (int i = 0; i < pageClans.Count; i++)
            {
                ClanData clan = pageClans[i];
                bool active = selected != null && clan.ClanId == selected.ClanId;
                int place = all.IndexOf(clan) + 1;
                string row = scroll + ".C" + i;
                UButton(c, scroll, viewCommand + clan.Tag, active ? ColSlotSolid : (i % 2 == 0 ? ColRowA : ColRowB), string.Empty, 1, ColText, 0, i * (rowH + 4f), rowW, rowH, row);
                if (active)
                    UPanel(c, row, null, ColAccent, 0, 0, 3, rowH);
                UText(c, row, "#" + place, 11, place <= 3 ? RankColor(place) : ColTextMute, TextAnchor.MiddleCenter, 4, 0, 34, rowH, true);
                UText(c, row, string.Format("<color={0}>[{1}]</color> {2}", TagColor, clan.Tag, EscapeRichText(Truncate(clan.DisplayName, 13))), 12, ColText, TextAnchor.MiddleLeft, 40, 6, rowW - 46f, 20, true);
                UText(c, row, string.Format("{0} очк.  •  {1}/{2}  •  в сети {3}", clan.Score, clan.Members.Count, _config.Clan.MaxMembers, clan.Members.Count(IsOnline)), 10, ColTextDim, TextAnchor.MiddleLeft, 40, 26, rowW - 46f, 18);
            }
        }

        private void DrawAdminClanDetail(CuiElementContainer c, BasePlayer player, ClanData clan)
        {
            string p = AdminDynamic;
            float x0 = 262f;
            float w = CW - x0;
            if (clan == null)
            {
                UIcon(c, p, IcoMembers, x0 + w / 2f - 32f, 170, 64);
                UText(c, p, "Кланов пока нет.", 14, ColTextDim, TextAnchor.MiddleCenter, x0, 244, w, 24);
                return;
            }

            List<ClanData> board = GetLeaderboard();
            int rank = board.FindIndex(x => x.ClanId == clan.ClanId) + 1;
            RelationshipManager.PlayerTeam team = GetManualGreenTeam(clan);
            int lands = _runtimeTerritories.Values.Count(t => string.Equals(t.State.OwnerTag, clan.Tag, StringComparison.OrdinalIgnoreCase));

            string head = p + ".Head";
            UPanel(c, p, head, ColPanel, x0, 0, w, 80, SpriteTile);
            UPanel(c, head, null, ColAccent, 0, 0, 3, 80);
            UAvatar(c, head, clan.OwnerId, 12, 10, 60);
            UText(c, head, string.Format("<color={0}>[{1}]</color>  {2}", TagColor, clan.Tag, EscapeRichText(Truncate(clan.DisplayName, 24))), 18, ColText, TextAnchor.MiddleLeft, 84, 6, w - 96f, 26, true);
            UText(c, head, string.Format("Глава: <color=#e8ded3>{0}</color>   •   Создан: {1}   •   Место: <color=#e8ded3>#{2}</color>", EscapeRichText(Truncate(GetKnownName(clan, clan.OwnerId), 18)), FormatDate(clan.CreatedUnix), rank), 11, ColTextDim, TextAnchor.MiddleLeft, 84, 32, w - 96f, 18);
            UText(c, head, string.Format("Зелёнка: {0}   •   Территорий: {1}   •   Захваты: {2}   •   PvP: {3}", team == null ? "<color=#ff6b5e>не привязана</color>" : "<color=#9fd36b>" + team.members.Count + "/8</color>", lands, clan.Captures, clan.Kills), 11, ColTextDim, TextAnchor.MiddleLeft, 84, 52, w - 96f, 18);

            float half = (w - 12f) / 2f;
            DrawAdminInputBlock(c, p, "rename", "НОВОЕ НАЗВАНИЕ (Enter)", "rrclan.adminui rename " + clan.Tag, 32, x0, 88, half, 60);
            DrawAdminInputBlock(c, p, "retag", "НОВЫЙ ТЕГ (Enter)", "rrclan.adminui retag " + clan.Tag, Math.Max(2, _config.Clan.MaxTagLength), x0 + half + 12f, 88, half, 60);

            string sc = p + ".Score";
            float scY = 156f, scH = 92f;
            UPanel(c, p, sc, ColPanel, x0, scY, w, scH, SpriteTile);
            UIcon(c, sc, IcoPoints, 12, 12, 40);
            UText(c, sc, "ОЧКИ", 11, ColTextDim, TextAnchor.MiddleLeft, 60, 10, 120, 16, true);
            UText(c, sc, clan.Score.ToString(), 24, ColText, TextAnchor.MiddleLeft, 60, 24, 160, 30, true);
            UText(c, sc, string.Format("PvP {0}  •  Захваты {1}\nУдержание {2}  •  Админ {3}{4}", clan.KillScore, clan.CaptureScore, clan.HoldScore, clan.AdminScore > 0 ? "+" : string.Empty, clan.AdminScore), 10, ColTextDim, TextAnchor.UpperLeft, 12, 58, 220, 30);

            int s = _config.AdminUi.SmallScoreStep, m = _config.AdminUi.MediumScoreStep, l = _config.AdminUi.LargeScoreStep;
            int[] steps = { -l, -m, -s, s, m, l };
            float bx = 236f;
            float bw = (w - bx - 12f - 5f * 4f) / 6f;
            for (int i = 0; i < steps.Length; i++)
            {
                bool plus = steps[i] > 0;
                UButton(c, sc, "rrclan.adminui score " + clan.Tag + " " + steps[i], plus ? ColGreenBtn : ColRedBtn, (plus ? "+" : string.Empty) + steps[i], 12, plus ? ColGreenTxt : ColRedTxt, bx + i * (bw + 4f), 10, bw, 30);
            }
            DrawInput(c, sc, sc + ".Input", "rrclan.adminui scoreinput " + clan.Tag, 9, bx, 48, 150, 32);
            UText(c, sc, "+50 — добавить, -20 — снять,\n=300 — поставить точно. Затем Enter.", 10, ColTextMute, TextAnchor.MiddleLeft, bx + 158f, 48, w - bx - 170f, 32);

            float listY = scY + scH + 8f;
            float bottomH = 36f;
            float listH = CH - listY - bottomH - 8f;
            List<ulong> members = clan.Members.OrderBy(id => GetRoleSort(clan, id)).ThenBy(id => GetKnownName(clan, id)).ToList();
            List<BasePlayer> candidates = clan.Members.Count >= _config.Clan.MaxMembers
                ? new List<BasePlayer>()
                : BasePlayer.activePlayerList.Where(target => target != null && target.IsConnected && GetClan(target.userID) == null).OrderBy(target => target.displayName).Take(12).ToList();

            float rowH = 46f;
            float addRowH = 38f;
            float contentH = members.Count * (rowH + 4f) + (candidates.Count > 0 ? 30f + candidates.Count * (addRowH + 4f) : 0f);
            string scroll = p + ".Members";
            UScroll(c, p, scroll, x0, listY, w, listH, contentH);
            float rowW = w - 12f;
            float yy = 0f;
            for (int i = 0; i < members.Count; i++)
            {
                ulong id = members[i];
                string row = scroll + ".M" + i;
                UPanel(c, scroll, row, i % 2 == 0 ? ColRowA : ColRowB, 0, yy, rowW, rowH, SpriteTile);
                if (id == clan.OwnerId)
                    UPanel(c, row, null, ColGold, 0, 0, 3, rowH);
                else if (id == clan.DeputyId)
                    UPanel(c, row, null, ColBlueTxt, 0, 0, 3, rowH);
                UAvatar(c, row, id, 8, 6, 34);
                UPanel(c, row, null, IsOnline(id) ? ColGreenDot : ColGrayDot, 34, 32, 9, 9, SpriteCircle);
                UText(c, row, EscapeRichText(Truncate(GetKnownName(clan, id), 18)), 12, ColText, TextAnchor.MiddleLeft, 50, 4, 210, 20, true);
                int kills = clan.PlayerKills != null && clan.PlayerKills.ContainsKey(id) ? clan.PlayerKills[id] : 0;
                UText(c, row, string.Format("{0}  •  PvP {1}  •  {2}", GetRoleName(clan, id), kills, id), 10, ColTextDim, TextAnchor.MiddleLeft, 50, 24, 230, 18);

                if (id != clan.OwnerId)
                {
                    float b = 58f;
                    float start = rowW - 8f - b * 4f - 12f;
                    bool confirmKick = IsConfirming(player, "admin:kick:" + id);
                    UButton(c, row, "rrclan.adminui leader " + clan.Tag + " " + id, "0.33 0.25 0.38 1", "ГЛАВА", 10, ColText, start, 9, b, 28);
                    UButton(c, row, "rrclan.adminui deputy " + clan.Tag + " " + id, ColBlueBtn, clan.DeputyId == id ? "−ЗАМ" : "ЗАМ", 10, ColBlueTxt, start + (b + 4f), 9, b, 28);
                    UButton(c, row, "rrclan.adminui role " + clan.Tag + " " + id, ColGrayBtn, "РОЛЬ", 10, ColText, start + (b + 4f) * 2f, 9, b, 28);
                    UButton(c, row, "rrclan.adminui kick " + clan.Tag + " " + id, confirmKick ? ColRedHot : ColRedBtn, confirmKick ? "ТОЧНО?" : "КИК", 10, ColRedTxt, start + (b + 4f) * 3f, 9, b, 28);
                }
                else
                    UText(c, row, "ГЛАВА КЛАНА", 11, ColGold, TextAnchor.MiddleRight, rowW - 200f, 0, 188, rowH, true);
                yy += rowH + 4f;
            }

            if (candidates.Count > 0)
            {
                UText(c, scroll, "ДОБАВИТЬ ИГРОКА (ОНЛАЙН, БЕЗ КЛАНА)", 10, ColTextMute, TextAnchor.MiddleLeft, 4, yy + 6f, rowW, 18, true);
                yy += 30f;
                for (int i = 0; i < candidates.Count; i++)
                {
                    BasePlayer target = candidates[i];
                    ulong targetId = target.userID;
                    string row = scroll + ".A" + i;
                    UPanel(c, scroll, row, ColPanelDark, 0, yy, rowW, addRowH);
                    UAvatar(c, row, targetId, 6, 5, 28);
                    UText(c, row, EscapeRichText(Truncate(target.displayName, 22)), 12, ColText, TextAnchor.MiddleLeft, 42, 0, rowW - 160f, addRowH);
                    UButton(c, row, "rrclan.adminui add " + clan.Tag + " " + targetId, ColGreenBtn, "ДОБАВИТЬ", 10, ColGreenTxt, rowW - 98f, 6, 90, 26);
                    yy += addRowH + 4f;
                }
            }

            float by = CH - bottomH;
            bool confirmReset = IsConfirming(player, "admin:resetstats:" + clan.ClanId);
            bool confirmDelete = IsConfirming(player, "admin:delete:" + clan.ClanId);
            float third = (w - 16f) / 3f;
            UButton(c, p, "rrclan.adminui greenview " + clan.Tag, ColGrayBtn, "ЗЕЛЁНКА КЛАНА", 11, ColText, x0, by, third, bottomH);
            UButton(c, p, "rrclan.adminui resetstats " + clan.Tag, confirmReset ? ColRedHot : "0.42 0.30 0.14 1", confirmReset ? "ТОЧНО СБРОСИТЬ?" : "СБРОСИТЬ СТАТИСТИКУ", 11, ColText, x0 + third + 8f, by, third, bottomH);
            UButton(c, p, "rrclan.adminui delete " + clan.Tag, confirmDelete ? ColRedHot : ColRedBtn, confirmDelete ? "ПОДТВЕРДИТЬ УДАЛЕНИЕ" : "УДАЛИТЬ КЛАН", 11, ColRedTxt, x0 + (third + 8f) * 2f, by, third, bottomH);
        }

        private void DrawAdminInputBlock(CuiElementContainer c, string parent, string key, string caption, string command, int limit, float x, float y, float w, float h)
        {
            string box = parent + ".In." + key;
            UPanel(c, parent, box, ColPanel, x, y, w, h, SpriteTile);
            UText(c, box, caption, 10, ColTextDim, TextAnchor.MiddleLeft, 12, 6, w - 24f, 14, true);
            DrawInput(c, box, box + ".Field", command, limit, 12, 24, w - 24f, 28);
        }

        private void DrawAdminGreenDetail(CuiElementContainer c, BasePlayer player, ClanData clan)
        {
            string p = AdminDynamic;
            float x0 = 262f;
            float w = CW - x0;
            if (clan == null)
            {
                UText(c, p, "Кланов пока нет.", 14, ColTextDim, TextAnchor.MiddleCenter, x0, 244, w, 24);
                return;
            }

            RelationshipManager.PlayerTeam team = GetManualGreenTeam(clan);
            string head = p + ".GHead";
            UPanel(c, p, head, ColPanel, x0, 0, w, 80, SpriteTile);
            UPanel(c, head, null, team == null ? ColRedBtn : ColGreenBtn, 0, 0, 3, 80);
            UIcon(c, head, IcoGreen, 14, 14, 52);
            UText(c, head, string.Format("<color={0}>[{1}]</color>  ЗЕЛЁНАЯ КОМАНДА", TagColor, clan.Tag), 16, ColText, TextAnchor.MiddleLeft, 80, 8, w - 92f, 24, true);
            if (team == null)
                UText(c, head, "<color=#ff6b5e>Команда не привязана.</color> Глава должен быть онлайн и лидером своей команды — затем нажмите ПРИВЯЗАТЬ.", 11, ColTextDim, TextAnchor.UpperLeft, 80, 34, w - 92f, 40);
            else
            {
                bool valid = team.teamLeader == clan.OwnerId && team.members.Contains(clan.OwnerId);
                UText(c, head, string.Format("Команда №{0}   •   Состав: {1}/8   •   Лидер: {2}", team.teamID, team.members.Count, EscapeRichText(Truncate(GetKnownName(clan, team.teamLeader), 16))), 11, ColTextDim, TextAnchor.MiddleLeft, 80, 34, w - 92f, 18);
                UText(c, head, valid ? "<color=#9fd36b>Глава клана — лидер команды.</color>" : "<color=#ff6b5e>Глава клана не лидер команды!</color>", 11, ColText, TextAnchor.MiddleLeft, 80, 54, w - 92f, 18);
            }

            float third = (w - 16f) / 3f;
            bool confirmUnbind = IsConfirming(player, "admin:unbind:" + clan.ClanId);
            UButton(c, p, "rrclan.adminui greenbind " + clan.Tag, ColGreenBtn, "ПРИВЯЗАТЬ КОМАНДУ ГЛАВЫ", 11, ColGreenTxt, x0, 88, third, 36);
            UButton(c, p, "rrclan.adminui greenleader " + clan.Tag, ColBlueBtn, "ВЕРНУТЬ ЛИДЕРА", 11, ColBlueTxt, x0 + third + 8f, 88, third, 36);
            UButton(c, p, "rrclan.adminui greenunbind " + clan.Tag, confirmUnbind ? ColRedHot : ColRedBtn, confirmUnbind ? "ТОЧНО ОТВЯЗАТЬ?" : "ОТВЯЗАТЬ", 11, ColRedTxt, x0 + (third + 8f) * 2f, 88, third, 36);

            UText(c, p, "ПРИГЛАСИТЬ — обычный запрос Rust.   ДОБАВИТЬ — сразу, только для игрока онлайн.", 10, ColTextMute, TextAnchor.MiddleLeft, x0, 132, w, 18);

            List<ulong> members = clan.Members.OrderBy(id => GetRoleSort(clan, id)).ThenBy(id => GetKnownName(clan, id)).ToList();
            float rowH = 50f;
            float viewY = 156f;
            string scroll = p + ".GList";
            UScroll(c, p, scroll, x0, viewY, w, CH - viewY, members.Count * (rowH + 4f));
            float rowW = w - 12f;
            for (int i = 0; i < members.Count; i++)
            {
                ulong id = members[i];
                bool inside = team != null && team.members.Contains(id);
                bool online = IsOnline(id);
                string row = scroll + ".G" + i;
                UPanel(c, scroll, row, i % 2 == 0 ? ColRowA : ColRowB, 0, i * (rowH + 4f), rowW, rowH, SpriteTile);
                UPanel(c, row, null, inside ? ColGreenBtn : ColBorder, 0, 0, 3, rowH);
                UAvatar(c, row, id, 8, 7, 36);
                UPanel(c, row, null, online ? ColGreenDot : ColGrayDot, 36, 34, 9, 9, SpriteCircle);
                UText(c, row, EscapeRichText(Truncate(GetKnownName(clan, id), 18)), 12, ColText, TextAnchor.MiddleLeft, 52, 6, 200, 20, true);
                UText(c, row, GetRoleName(clan, id) + (inside ? "  •  <color=#c8eb80>в команде</color>" : "  •  не в команде"), 10, ColTextDim, TextAnchor.MiddleLeft, 52, 26, 220, 18);

                if (id == clan.OwnerId)
                    UText(c, row, inside ? "ГЛАВА В КОМАНДЕ" : "ГЛАВА ВНЕ КОМАНДЫ!", 11, inside ? ColGold : ColRedTimer, TextAnchor.MiddleRight, rowW - 220f, 0, 208, rowH, true);
                else if (team == null)
                    UText(c, row, "нет команды", 11, ColTextMute, TextAnchor.MiddleRight, rowW - 220f, 0, 208, rowH);
                else if (inside)
                    UButton(c, row, "rrclan.adminui greenkick " + clan.Tag + " " + id, ColRedBtn, "УБРАТЬ ИЗ КОМАНДЫ", 10, ColRedTxt, rowW - 188f, 10, 180, 30);
                else
                {
                    UButton(c, row, "rrclan.adminui greeninvite " + clan.Tag + " " + id, ColGreenBtn, "ПРИГЛАСИТЬ", 10, ColGreenTxt, rowW - 188f, 10, 88, 30);
                    UButton(c, row, online ? "rrclan.adminui greenforceadd " + clan.Tag + " " + id : string.Empty, online ? ColBlueBtn : ColDisabledBtn, online ? "ДОБАВИТЬ" : "НЕ В СЕТИ", 10, online ? ColBlueTxt : ColDisabledTxt, rowW - 96f, 10, 88, 30);
                }
            }
        }

        private void DrawAdminTerritories(CuiElementContainer c, BasePlayer player, ClanData selected)
        {
            string p = AdminDynamic;
            DrawTitle(c, p, "ТЕРРИТОРИИ", "Точка ставится там, где вы стоите. «Отдать» — клану, выбранному во вкладке КЛАНЫ: " + (selected == null ? "не выбран" : "[" + selected.Tag + "]"));
            UButton(c, p, "rrclan.adminui rebuild", ColGrayBtn, "ПЕРЕСОЗДАТЬ ПЛОЩАДКИ", 11, ColText, CW - 190f, 8, 190, 30);

            List<TerritoryConfig> list = _config.Territories.Where(t => t != null).OrderByDescending(t => t.Enabled).ThenBy(t => t.DisplayName).Take(30).ToList();
            float rowH = 112f;
            string scroll = p + ".TList";
            UScroll(c, p, scroll, 0, 62, CW, CH - 62f, list.Count * (rowH + 8f));
            float rowW = CW - 12f;
            long now = UnixNow();
            for (int i = 0; i < list.Count; i++)
            {
                TerritoryConfig cfg = list[i];
                RuntimeTerritory rt;
                _runtimeTerritories.TryGetValue(cfg.Id, out rt);
                TerritoryState st = rt != null ? rt.State : GetOrCreateTerritoryState(cfg.Id);
                bool capturing = !string.IsNullOrEmpty(st.CapturingTag);
                string row = scroll + ".T" + i;
                UPanel(c, scroll, row, cfg.Enabled ? ColPanel : ColPanelDark, 0, i * (rowH + 8f), rowW, rowH, SpriteTile);
                UPanel(c, row, null, !cfg.Enabled ? ColBorder : capturing ? ColAccent : ColGreenBtn, 0, 0, 3, rowH);
                UPanel(c, row, null, ColPanelDark, 12, 12, 88, 88, SpriteTile);
                UIcon(c, row, TerritoryIcon(cfg), 22, 22, 68);

                UText(c, row, EscapeRichText(cfg.DisplayName.ToUpperInvariant()) + "  <color=#736d64>" + cfg.Id + "</color>", 15, cfg.Enabled ? ColText : ColTextMute, TextAnchor.MiddleLeft, 112, 8, 300, 22, true);
                string mon = rt == null ? (cfg.Enabled ? "не загружен" : "<color=#9a948a>ВЫКЛЮЧЕНА</color>") : rt.Monument == null ? "<color=#ff6b5e>РТ не найден на карте</color>" : "<color=#9fd36b>РТ найден</color>";
                string point = cfg.Calibrated ? (rt != null && rt.Monument != null ? "<color=#9fd36b>точка " + FormatVector(rt.CapturePoint) + "</color>" : "<color=#9fd36b>точка настроена</color>") : "<color=#ff6b5e>ТОЧКА НЕ НАСТРОЕНА</color>";
                UText(c, row, mon + "   •   " + point + "   •   радиус " + Mathf.RoundToInt(cfg.Radius) + " м", 11, ColTextDim, TextAnchor.MiddleLeft, 112, 32, 360, 18);
                string owner = string.IsNullOrEmpty(st.OwnerTag) ? "нейтральная" : "<color=" + TagColor + ">[" + st.OwnerTag + "]</color>";
                string state = capturing
                    ? string.Format("захват <color={0}>[{1}]</color> {2}% {3}", TagColor, st.CapturingTag, CapturePercent(st), rt != null ? GetCaptureStatusShort(rt) : string.Empty)
                    : st.CooldownUntilUnix > now ? "<color=#ffb35a>КД " + FormatDuration(st.CooldownUntilUnix - now) + "</color>" : "свободна";
                UText(c, row, "Владелец: " + owner + "   •   " + state, 11, ColTextDim, TextAnchor.MiddleLeft, 112, 52, 360, 18);
                if (rt != null && rt.Monument != null && capturing)
                    DrawBar(c, row, 112, 80, 340, 10, CaptureRatio(st), ColAccent);

                float gx = rowW - 8f - 4f * 88f - 3f * 4f;
                float b = 88f;
                UButton(c, row, "rrclan.adminui tsetpoint " + cfg.Id, ColBlueBtn, "ТОЧКА ЗДЕСЬ", 10, ColBlueTxt, gx, 14, b, 34);
                UButton(c, row, rt != null && rt.Monument != null ? "rrclan.adminui ttp " + cfg.Id : string.Empty, rt != null && rt.Monument != null ? ColGrayBtn : ColDisabledBtn, "ТЕЛЕПОРТ", 10, rt != null && rt.Monument != null ? ColText : ColDisabledTxt, gx + (b + 4f), 14, b, 34);
                UButton(c, row, "rrclan.adminui ttoggle " + cfg.Id, cfg.Enabled ? ColRedBtn : ColGreenBtn, cfg.Enabled ? "ВЫКЛЮЧИТЬ" : "ВКЛЮЧИТЬ", 10, cfg.Enabled ? ColRedTxt : ColGreenTxt, gx + (b + 4f) * 2f, 14, b, 34);
                UButton(c, row, st.CooldownUntilUnix > now ? "rrclan.adminui tclearcd " + cfg.Id : string.Empty, st.CooldownUntilUnix > now ? ColGrayBtn : ColDisabledBtn, "СНЯТЬ КД", 10, st.CooldownUntilUnix > now ? ColText : ColDisabledTxt, gx + (b + 4f) * 3f, 14, b, 34);
                UButton(c, row, capturing ? "rrclan.adminui tcancel " + cfg.Id : string.Empty, capturing ? ColRedBtn : ColDisabledBtn, "ОТМЕНИТЬ ЗАХВАТ", 10, capturing ? ColRedTxt : ColDisabledTxt, gx, 60, b, 34);
                UButton(c, row, !string.IsNullOrEmpty(st.OwnerTag) ? "rrclan.adminui tclearowner " + cfg.Id : string.Empty, !string.IsNullOrEmpty(st.OwnerTag) ? ColGrayBtn : ColDisabledBtn, "СБРОС ВЛАДЕЛЬЦА", 10, !string.IsNullOrEmpty(st.OwnerTag) ? ColText : ColDisabledTxt, gx + (b + 4f), 60, b, 34);
                bool canGive = selected != null && cfg.Enabled;
                UButton(c, row, canGive ? "rrclan.adminui tgive " + cfg.Id : string.Empty, canGive ? ColAccent : ColDisabledBtn, selected == null ? "ОТДАТЬ: КЛАН НЕ ВЫБРАН" : "ОТДАТЬ [" + selected.Tag + "]", 10, canGive ? "1 0.92 0.86 1" : ColDisabledTxt, gx + (b + 4f) * 2f, 60, b * 2f + 4f, 34);
            }
        }

        private TerritoryState GetOrCreateTerritoryState(string id)
        {
            TerritoryState state;
            if (!_data.Territories.TryGetValue(id, out state) || state == null)
            {
                state = new TerritoryState { Id = id };
                _data.Territories[id] = state;
            }
            return state;
        }

        private void AdminTerritoryAction(BasePlayer admin, string action, string id)
        {
            TerritoryConfig cfg = _config.Territories.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            if (cfg == null)
            {
                SendReply(admin, "Территория не найдена.");
                return;
            }

            RuntimeTerritory rt;
            _runtimeTerritories.TryGetValue(cfg.Id, out rt);
            TerritoryState st = rt != null ? rt.State : GetOrCreateTerritoryState(cfg.Id);
            long now = UnixNow();

            switch (action)
            {
                case "tsetpoint":
                    SetTerritoryPoint(admin, cfg.Id);
                    return;

                case "ttp":
                {
                    if (rt == null || rt.Monument == null)
                    {
                        SendReply(admin, "РТ не найден на карте или территория выключена.");
                        return;
                    }
                    Vector3 target = rt.Config.Calibrated ? rt.CapturePoint : rt.Monument.transform.position;
                    if (!rt.Config.Calibrated && TerrainMeta.HeightMap != null)
                        target.y = Mathf.Max(target.y, TerrainMeta.HeightMap.GetHeight(target));
                    admin.Teleport(target + Vector3.up * 1.5f);
                    SendReply(admin, string.Format("Телепорт к «{0}». Чтобы поставить точку генератора — встаньте в нужное место и откройте /clanadmin → ТЕРРИТОРИИ → ТОЧКА ЗДЕСЬ.", cfg.DisplayName));
                    return;
                }

                case "ttoggle":
                    if (cfg.Enabled && rt != null)
                    {
                        if (!string.IsNullOrEmpty(rt.State.CapturingTag))
                            CancelCapture(rt, "Территория выключена администратором.", false);
                        KillCaptureGenerator(rt, false);
                        KillCaptureStation(rt);
                    }
                    cfg.Enabled = !cfg.Enabled;
                    SaveConfig();
                    BuildRuntimeTerritories();
                    UpdateCaptureHudForAll();
                    SendReply(admin, string.Format("«{0}» {1}.", cfg.DisplayName, cfg.Enabled ? "включена" : "выключена"));
                    return;

                case "tcancel":
                    if (rt == null || string.IsNullOrEmpty(rt.State.CapturingTag))
                    {
                        SendReply(admin, "Здесь нет активного захвата.");
                        return;
                    }
                    CancelCapture(rt, "Захват отменён администратором.", false);
                    return;

                case "tclearowner":
                {
                    string old = st.OwnerTag;
                    st.OwnerTag = string.Empty;
                    st.HoldTicksAwarded = 0;
                    st.NextHoldRewardUnix = 0;
                    if (string.IsNullOrEmpty(st.CapturingTag))
                        st.Status = "idle";
                    AddStatisticsEvent("ADMIN", old, rt, string.Format("{0} снял владельца [{1}] с «{2}».", admin.displayName, old, cfg.DisplayName));
                    SaveData();
                    SendReply(admin, string.Format("«{0}» теперь нейтральная.", cfg.DisplayName));
                    return;
                }

                case "tclearcd":
                    st.CooldownUntilUnix = 0;
                    SaveData();
                    SendReply(admin, string.Format("Перезарядка «{0}» снята.", cfg.DisplayName));
                    return;

                case "tgive":
                {
                    string selectedTag;
                    ClanData clan = _adminSelectedClans.TryGetValue(admin.userID, out selectedTag) ? GetClanByTag(selectedTag) : null;
                    if (clan == null)
                    {
                        SendReply(admin, "Сначала выберите клан во вкладке КЛАНЫ.");
                        return;
                    }
                    if (rt != null && !string.IsNullOrEmpty(rt.State.CapturingTag))
                        CancelCapture(rt, "Территория передана администратором.", false);
                    st.OwnerTag = clan.Tag;
                    st.Status = "owned";
                    st.LastCapturedUnix = now;
                    st.HoldTicksAwarded = 0;
                    st.NextHoldRewardUnix = now + _config.Score.HoldRewardIntervalSeconds;
                    AddStatisticsEvent("ADMIN", clan.Tag, rt, string.Format("{0} отдал «{1}» клану [{2}].", admin.displayName, cfg.DisplayName, clan.Tag));
                    SaveData();
                    BroadcastToClan(clan, string.Format("Администратор передал вашему клану территорию {0}.", cfg.DisplayName));
                    SendReply(admin, string.Format("«{0}» передана клану [{1}].", cfg.DisplayName, clan.Tag));
                    return;
                }
            }
        }

        private class AdminToggle
        {
            public string Key;
            public string Title;
            public string Hint;
            public Func<bool> Get;
            public Action<bool> Set;
        }

        private class AdminNumber
        {
            public string Key;
            public string Title;
            public string Unit;
            public int Step;
            public int Divider = 1;
            public int Min;
            public int Max = 1000000;
            public Func<int> Get;
            public Action<int> Set;
        }

        private List<AdminToggle> GetAdminToggles()
        {
            return new List<AdminToggle>
            {
                new AdminToggle { Key = "public", Title = "Система открыта для всех игроков", Hint = "Выкл — кланами пользуются только администраторы (тест)", Get = () => _config.Access.EnableForRegularPlayers, Set = v => _config.Access.EnableForRegularPlayers = v },
                new AdminToggle { Key = "admintest", Title = "Тестирование администраторами", Hint = "Пока система закрыта, админы видят и используют её", Get = () => _config.Access.AllowAdministratorTesting, Set = v => _config.Access.AllowAdministratorTesting = v },
                new AdminToggle { Key = "requiregreen", Title = "Нужна зелёная команда для создания", Hint = "Клан создаёт только лидер своей команды Rust", Get = () => _config.Clan.RequireGreenTeamBeforeClanCreation, Set = v => _config.Clan.RequireGreenTeamBeforeClanCreation = v },
                new AdminToggle { Key = "kickgreen", Title = "Убирать ушедших из зелёной команды", Hint = "Исключённый или вышедший игрок покидает и команду", Get = () => _config.Clan.RemoveFromGreenTeamOnLeave, Set = v => _config.Clan.RemoveFromGreenTeamOnLeave = v },
                new AdminToggle { Key = "takeover", Title = "Перехват чужого захвата", Hint = "Другой клан может перехватить активный захват", Get = () => _config.Capture.AllowCaptureTakeover, Set = v => _config.Capture.AllowCaptureTakeover = v },
                new AdminToggle { Key = "takeoverempty", Title = "Перехват только без ТНК", Hint = "Сначала нужно вынуть всё топливо из генератора", Get = () => _config.Capture.TakeoverRequiresEmptyFuel, Set = v => _config.Capture.TakeoverRequiresEmptyFuel = v },
                new AdminToggle { Key = "enemyfuel", Title = "Враги могут забирать ТНК", Hint = "Иначе топливо трогает только захватывающий клан", Get = () => _config.Capture.AllowEnemyFuelLooting, Set = v => _config.Capture.AllowEnemyFuelLooting = v },
                new AdminToggle { Key = "gencancel", Title = "Срыв захвата при уничтожении генератора", Hint = "Выкл — генератор ставится заново, захват идёт дальше", Get = () => _config.Capture.CancelOnGeneratorDestroyed, Set = v => _config.Capture.CancelOnGeneratorDestroyed = v },
                new AdminToggle { Key = "holdpause", Title = "Пауза удержания во время чужого захвата", Hint = "Владелец не получает очки, пока РТ захватывают", Get = () => _config.Score.PauseHoldWhileEnemyCapturing, Set = v => _config.Score.PauseHoldWhileEnemyCapturing = v },
                new AdminToggle { Key = "station", Title = "Площадка из мешков на точке", Hint = "Постоянное ограждение вокруг точки генератора", Get = () => _config.Capture.ShowCaptureStation, Set = v => _config.Capture.ShowCaptureStation = v },
                new AdminToggle { Key = "chattag", Title = "Тег клана в общем чате", Hint = "[TAG] перед ником (работает и с BetterChat)", Get = () => _config.Chat.EnableGlobalTag, Set = v => _config.Chat.EnableGlobalTag = v },
                new AdminToggle { Key = "chatlog", Title = "Клановый чат в консоль сервера", Hint = "Сообщения /c видны в консоли", Get = () => _config.Chat.LogClanChatToServerConsole, Set = v => _config.Chat.LogClanChatToServerConsole = v },
                new AdminToggle { Key = "wipereset", Title = "Сброс рейтинга на вайпе", Hint = "Очки и территории обнуляются при новой карте", Get = () => _config.Score.ResetOnNewSave, Set = v => _config.Score.ResetOnNewSave = v }
            };
        }

        private List<AdminNumber> GetAdminNumbers()
        {
            return new List<AdminNumber>
            {
                new AdminNumber { Key = "kill", Title = "Очки за убийство", Unit = "очк.", Step = 1, Max = 10000, Get = () => _config.Score.KillPoints, Set = v => _config.Score.KillPoints = v },
                new AdminNumber { Key = "capture", Title = "Очки за захват", Unit = "очк.", Step = 5, Max = 100000, Get = () => _config.Score.CapturePoints, Set = v => _config.Score.CapturePoints = v },
                new AdminNumber { Key = "takeoverbonus", Title = "Бонус за отбор территории", Unit = "очк.", Step = 5, Max = 100000, Get = () => _config.Score.TakeoverBonusPoints, Set = v => _config.Score.TakeoverBonusPoints = v },
                new AdminNumber { Key = "hold", Title = "Очки за удержание", Unit = "очк.", Step = 1, Max = 10000, Get = () => _config.Score.HoldPoints, Set = v => _config.Score.HoldPoints = v },
                new AdminNumber { Key = "holdint", Title = "Интервал удержания", Unit = "мин", Step = 60, Divider = 60, Min = 60, Max = 86400, Get = () => _config.Score.HoldRewardIntervalSeconds, Set = v => _config.Score.HoldRewardIntervalSeconds = v },
                new AdminNumber { Key = "victimcd", Title = "Защита от фарма одной жертвы", Unit = "мин", Step = 300, Divider = 60, Max = 86400, Get = () => _config.Score.SameVictimCooldownSeconds, Set = v => _config.Score.SameVictimCooldownSeconds = v },
                new AdminNumber { Key = "duration", Title = "Длительность захвата", Unit = "мин", Step = 60, Divider = 60, Min = 60, Max = 86400, Get = () => _config.Capture.CaptureDurationSeconds, Set = v => _config.Capture.CaptureDurationSeconds = v },
                new AdminNumber { Key = "fuel", Title = "ТНК за одну проверку", Unit = "шт.", Step = 1, Min = 1, Max = 500, Get = () => _config.Capture.FuelPerTick, Set = v => _config.Capture.FuelPerTick = v },
                new AdminNumber { Key = "minstart", Title = "Участников для старта", Unit = "чел.", Step = 1, Min = 1, Max = 8, Get = () => _config.Capture.MinimumMembersToStart, Set = v => _config.Capture.MinimumMembersToStart = v },
                new AdminNumber { Key = "minprogress", Title = "Участников для прогресса", Unit = "чел.", Step = 1, Min = 1, Max = 8, Get = () => _config.Capture.MinimumMembersToProgress, Set = v => _config.Capture.MinimumMembersToProgress = v },
                new AdminNumber { Key = "cooldown", Title = "КД территории после захвата", Unit = "мин", Step = 600, Divider = 60, Max = 604800, Get = () => _config.Capture.TerritoryCooldownSeconds, Set = v => _config.Capture.TerritoryCooldownSeconds = v },
                new AdminNumber { Key = "failcd", Title = "КД после сорванного захвата", Unit = "мин", Step = 60, Divider = 60, Max = 86400, Get = () => _config.Capture.FailedCaptureCooldownSeconds, Set = v => _config.Capture.FailedCaptureCooldownSeconds = v },
                new AdminNumber { Key = "genhp", Title = "Здоровье генератора", Unit = "HP", Step = 250, Min = 100, Max = 100000, Get = () => Mathf.RoundToInt(_config.Capture.GeneratorHealth), Set = v => _config.Capture.GeneratorHealth = v },
                new AdminNumber { Key = "maxmembers", Title = "Максимум участников", Unit = "чел.", Step = 1, Min = 2, Max = 8, Get = () => _config.Clan.MaxMembers, Set = v => _config.Clan.MaxMembers = v }
            };
        }

        private void AdminToggleSetting(BasePlayer admin, string key)
        {
            AdminToggle toggle = GetAdminToggles().FirstOrDefault(t => t.Key == key);
            if (toggle == null)
                return;

            bool value = !toggle.Get();
            toggle.Set(value);
            NormalizeConfig();
            SaveConfig();
            SendReply(admin, string.Format("{0}: <b>{1}</b>.", toggle.Title, value ? "ВКЛ" : "ВЫКЛ"));

            if (key == "public" || key == "admintest" || key == "station")
                ApplyAccessChange();
        }

        private void ApplyAccessChange()
        {
            ResolveCaptureStationPrefabPath();
            BuildRuntimeTerritories();
            foreach (BasePlayer target in BasePlayer.activePlayerList.ToList())
            {
                if (target == null || !target.IsConnected)
                    continue;
                ulong id = target.userID;
                if (!CanAccessSystem(target) && _clanUiOpen.Contains(id))
                    CloseClanUi(target);
            }
            UpdateCaptureHudForAll();
        }

        private void AdminChangeNumber(BasePlayer admin, string key, int direction)
        {
            AdminNumber number = GetAdminNumbers().FirstOrDefault(n => n.Key == key);
            if (number == null || direction == 0)
                return;

            int value = Mathf.Clamp(number.Get() + Math.Sign(direction) * number.Step, number.Min, number.Max);
            number.Set(value);
            NormalizeConfig();
            SaveConfig();
            int stored = number.Get();
            SendReply(admin, string.Format("{0}: <b>{1}</b>.", number.Title, FormatAdminNumber(number, stored)));
        }

        private string FormatAdminNumber(AdminNumber number, int value)
        {
            if (number.Divider > 1)
            {
                if (value % number.Divider == 0)
                    return (value / number.Divider) + " " + number.Unit;
                return (value / (float)number.Divider).ToString("0.#", CultureInfo.InvariantCulture) + " " + number.Unit;
            }
            return value + " " + number.Unit;
        }

        private void DrawAdminSettings(CuiElementContainer c, BasePlayer player)
        {
            string p = AdminDynamic;
            DrawTitle(c, p, "НАСТРОЙКИ", "Изменения сразу сохраняются в oxide/config/RockRustClans.json");

            float half = (CW - 12f) / 2f;
            float viewY = 62f;
            float bottomH = 40f;
            float viewH = CH - viewY - bottomH - 10f;
            float rowH = 48f;

            List<AdminToggle> toggles = GetAdminToggles();
            string left = p + ".Toggles";
            UScroll(c, p, left, 0, viewY, half, viewH, toggles.Count * (rowH + 4f));
            float rowW = half - 12f;
            for (int i = 0; i < toggles.Count; i++)
            {
                AdminToggle toggle = toggles[i];
                bool on = toggle.Get();
                string row = left + ".T" + i;
                UPanel(c, left, row, i % 2 == 0 ? ColRowA : ColRowB, 0, i * (rowH + 4f), rowW, rowH, SpriteTile);
                UPanel(c, row, null, on ? ColGreenBtn : ColBorder, 0, 0, 3, rowH);
                UText(c, row, toggle.Title, 12, ColText, TextAnchor.MiddleLeft, 12, 5, rowW - 100f, 20, true);
                UText(c, row, toggle.Hint, 10, ColTextDim, TextAnchor.MiddleLeft, 12, 25, rowW - 100f, 18);
                UButton(c, row, "rrclan.adminui stoggle " + toggle.Key, on ? ColGreenBtn : ColGrayBtn, on ? "ВКЛ" : "ВЫКЛ", 12, on ? ColGreenTxt : ColTextDim, rowW - 82f, 10, 72, 28);
            }

            List<AdminNumber> numbers = GetAdminNumbers();
            string right = p + ".Numbers";
            UScroll(c, p, right, half + 12f, viewY, half, viewH, numbers.Count * (rowH + 4f));
            for (int i = 0; i < numbers.Count; i++)
            {
                AdminNumber number = numbers[i];
                string row = right + ".N" + i;
                UPanel(c, right, row, i % 2 == 0 ? ColRowA : ColRowB, 0, i * (rowH + 4f), rowW, rowH, SpriteTile);
                UText(c, row, number.Title, 12, ColText, TextAnchor.MiddleLeft, 12, 0, rowW - 190f, rowH, true);
                UButton(c, row, "rrclan.adminui snum " + number.Key + " -1", ColRedBtn, "−", 16, ColRedTxt, rowW - 176f, 9, 32, 30);
                UText(c, row, FormatAdminNumber(number, number.Get()), 13, ColText, TextAnchor.MiddleCenter, rowW - 140f, 0, 92, rowH, true);
                UButton(c, row, "rrclan.adminui snum " + number.Key + " 1", ColGreenBtn, "+", 16, ColGreenTxt, rowW - 44f, 9, 32, 30);
            }

            float by = CH - bottomH;
            float third = (CW - 16f) / 3f;
            bool confirmSeason = IsConfirming(player, "admin:seasonreset");
            bool confirmStats = IsConfirming(player, "admin:statsreset");
            UButton(c, p, "rrclan.adminui rebuild", ColGrayBtn, "ПЕРЕСОЗДАТЬ ПЛОЩАДКИ", 12, ColText, 0, by, third, bottomH);
            UButton(c, p, "rrclan.adminui statsreset", confirmStats ? ColRedHot : "0.42 0.30 0.14 1", confirmStats ? "ТОЧНО ОЧИСТИТЬ?" : "ОЧИСТИТЬ СТАТИСТИКУ", 12, ColText, third + 8f, by, third, bottomH);
            UButton(c, p, "rrclan.adminui seasonreset", confirmSeason ? ColRedHot : ColRedBtn, confirmSeason ? "ТОЧНО СБРОСИТЬ СЕЗОН?" : "СБРОСИТЬ СЕЗОН (ОЧКИ И РТ)", 12, ColRedTxt, (third + 8f) * 2f, by, third, bottomH);
        }

        private void DrawAdminStatistics(CuiElementContainer c, BasePlayer player)
        {
            EnsureStatisticsStorage();
            string p = AdminDynamic;
            PluginStatistics stats = _data.Statistics;
            int activeCaptures = _runtimeTerritories.Values.Count(t => !string.IsNullOrEmpty(t.State.CapturingTag));
            int inClans = _data.Clans.Values.Sum(clan => clan.Members.Count);
            DrawTitle(c, p, "СТАТИСТИКА", string.Format("Игроков в кланах: {0}   •   Последняя активность: {1}", inClans, stats.LastActivityUnix > 0 ? FormatDuration(UnixNow() - stats.LastActivityUnix) + " назад" : "—"));

            string[] titles = { "КЛАНОВ", "ИДЁТ ЗАХВАТОВ", "ПОПЫТОК", "УСПЕШНО", "НЕУСПЕШНО", "PVP-УБИЙСТВ" };
            string[] values =
            {
                _data.Clans.Count.ToString(),
                activeCaptures.ToString(),
                stats.CaptureAttempts.ToString(),
                stats.CaptureSuccesses.ToString(),
                (stats.CaptureFailures + stats.CaptureManualCancels).ToString(),
                stats.PvpKills.ToString()
            };
            string[][] icons = { IcoMembers, IcoCapture, IcoFuel, IcoRank, IcoCapture, IcoPvp };
            string[] colors = { ColText, ColOrangeTxt, ColText, ColGreenTxt, ColRedTimer, ColBlueTxt };
            float cw = (CW - 5f * 8f) / 6f;
            for (int i = 0; i < titles.Length; i++)
            {
                string card = p + ".Sum" + i;
                UPanel(c, p, card, ColPanel, i * (cw + 8f), 62, cw, 66, SpriteTile);
                UIcon(c, card, icons[i], 8, 17, 32);
                UText(c, card, titles[i], 9, ColTextDim, TextAnchor.MiddleLeft, 46, 8, cw - 50f, 16, true);
                UText(c, card, values[i], 20, colors[i], TextAnchor.MiddleLeft, 46, 26, cw - 50f, 30, true);
            }

            float y = 140f;
            List<RuntimeTerritory> active = _runtimeTerritories.Values.Where(t => !string.IsNullOrEmpty(t.State.CapturingTag)).OrderBy(t => t.Config.DisplayName).ToList();
            string act = p + ".Active";
            UPanel(c, p, act, ColPanel, 0, y, CW, 54, SpriteTile);
            UText(c, act, "СЕЙЧАС", 10, ColTextDim, TextAnchor.MiddleLeft, 12, 0, 70, 54, true);
            if (active.Count == 0)
                UText(c, act, "Активных захватов нет.", 12, ColTextMute, TextAnchor.MiddleLeft, 82, 0, CW - 90f, 54);
            else
            {
                string text = string.Join("     ", active.Take(3).Select(t => string.Format("<color={0}>[{1}]</color> {2} {3}% {4}", TagColor, t.State.CapturingTag, EscapeRichText(t.Config.DisplayName), CapturePercent(t.State), GetCaptureStatusShort(t))).ToArray());
                UText(c, act, text, 12, ColText, TextAnchor.MiddleLeft, 82, 0, CW - 90f, 54);
            }

            y += 64f;
            float graphH = 150f;
            float half = (CW - 12f) / 2f;
            DrawStatisticsGraph(c, p, "pvp", true, 0, y, half, graphH);
            DrawStatisticsGraph(c, p, "cap", false, half + 12f, y, half, graphH);

            y += graphH + 10f;
            UText(c, p, "ЖУРНАЛ СОБЫТИЙ", 11, ColText, TextAnchor.MiddleLeft, 0, y, 200, 20, true);
            UText(c, p, string.Format("Очки: PvP {0}  •  захваты {1}  •  удержание {2}  •  перехватов {3}  •  отказов {4}  •  генератор уничтожен {5}", stats.PvpPoints, stats.CapturePoints, stats.HoldPoints, stats.CaptureTakeovers, stats.CaptureManualCancels, stats.GeneratorDestroyedFailures), 10, ColTextMute, TextAnchor.MiddleRight, 200, y, CW - 200f, 20);
            y += 24f;

            const int logPageSize = 25;
            List<PluginEventEntry> allEvents = _data.StatisticsEvents.Where(x => x != null).OrderByDescending(x => x.Unix).ToList();
            int logPages = Math.Max(1, (allEvents.Count + logPageSize - 1) / logPageSize);
            int logPage;
            _adminLogPages.TryGetValue(player.userID, out logPage);
            logPage = Mathf.Clamp(logPage, 0, logPages - 1);
            List<PluginEventEntry> events = allEvents.Skip(logPage * logPageSize).Take(logPageSize).ToList();

            float pagerH = logPages > 1 ? 34f : 0f;
            float rowH = 26f;
            string scroll = p + ".Log";
            UScroll(c, p, scroll, 0, y, CW, CH - y - pagerH, events.Count * (rowH + 2f));
            float rowW = CW - 12f;
            long now = UnixNow();
            for (int i = 0; i < events.Count; i++)
            {
                PluginEventEntry entry = events[i];
                // строка = фон + текст
                if (i % 2 == 0)
                    UPanel(c, scroll, null, ColRowA, 0, i * (rowH + 2f), rowW, rowH);
                string color = entry.Type == "SUCCESS" ? "#9fd36b" : entry.Type == "FAIL" || entry.Type == "CANCEL" ? "#ff6b5e" : entry.Type == "PVP" ? "#8fc3f0" : entry.Type == "ADMIN" ? "#efc34d" : "#d6a84b";
                UText(c, scroll, string.Format("<color={0}><b>{1}</b></color>   <color=#736d64>{2} назад</color>   {3}", color, EscapeRichText(EventTypeName(entry.Type)), FormatDuration(Math.Max(0, now - entry.Unix)), EscapeRichText(entry.Text)), 11, ColText, TextAnchor.MiddleLeft, 10, i * (rowH + 2f), rowW - 20f, rowH);
            }
            if (logPages > 1)
            {
                float py = CH - 30f;
                UButton(c, p, logPage > 0 ? "rrclan.adminui logpage " + (logPage - 1) : string.Empty, logPage > 0 ? ColGrayBtn : ColDisabledBtn, "‹ НОВЕЕ", 11, logPage > 0 ? ColText : ColDisabledTxt, 0, py, 110, 30);
                UText(c, p, string.Format("стр. {0} из {1}", logPage + 1, logPages), 11, ColTextDim, TextAnchor.MiddleCenter, 120, py, CW - 240f, 30, true);
                UButton(c, p, logPage < logPages - 1 ? "rrclan.adminui logpage " + (logPage + 1) : string.Empty, logPage < logPages - 1 ? ColGrayBtn : ColDisabledBtn, "СТАРЕЕ ›", 11, logPage < logPages - 1 ? ColText : ColDisabledTxt, CW - 110f, py, 110, 30);
            }
        }

        private string EventTypeName(string type)
        {
            switch (type)
            {
                case "START": return "СТАРТ";
                case "SUCCESS": return "УСПЕХ";
                case "FAIL": return "СРЫВ";
                case "CANCEL": return "ОТКАЗ";
                case "TAKEOVER": return "ПЕРЕХВАТ";
                case "PVP": return "PVP";
                case "ADMIN": return "АДМИН";
                default: return type ?? string.Empty;
            }
        }

        private void DrawStatisticsGraph(CuiElementContainer c, string parent, string key, bool pvp, float x, float y, float w, float h)
        {
            string box = parent + ".Graph." + key;
            UPanel(c, parent, box, ColPanel, x, y, w, h, SpriteTile);
            int hours = _config.AdminUi.StatisticsGraphHours;
            long currentHour = (UnixNow() / 3600L) * 3600L;
            List<HourlyStatisticsBucket> buckets = new List<HourlyStatisticsBucket>();
            for (int i = hours - 1; i >= 0; i--)
            {
                long hour = currentHour - i * 3600L;
                HourlyStatisticsBucket found = _data.HourlyStatistics.FirstOrDefault(b => b != null && b.HourUnix == hour);
                buckets.Add(found ?? new HourlyStatisticsBucket { HourUnix = hour });
            }

            int maxValue = 1;
            int total = 0;
            foreach (HourlyStatisticsBucket bucket in buckets)
            {
                int value = pvp ? bucket.PvpKills : bucket.CaptureStarts;
                total += value;
                if (value > maxValue) maxValue = value;
            }

            UText(c, box, pvp ? "PVP-УБИЙСТВА ПО ЧАСАМ" : "ЗАХВАТЫ ПО ЧАСАМ", 11, ColText, TextAnchor.MiddleLeft, 12, 6, w - 120f, 18, true);
            UText(c, box, "за " + hours + " ч: " + total, 10, ColTextDim, TextAnchor.MiddleRight, w - 130f, 6, 118, 18);

            float left = 12f, right = w - 12f, top = 30f, bottom = h - 24f;
            float plotH = bottom - top;
            float step = (right - left) / Math.Max(1, buckets.Count);
            UPanel(c, box, null, ColBorder, left, bottom, right - left, 1);
            for (int i = 0; i < buckets.Count; i++)
            {
                HourlyStatisticsBucket bucket = buckets[i];
                int value = pvp ? bucket.PvpKills : bucket.CaptureStarts;
                if (value <= 0)
                    continue;
                float barH = plotH * value / maxValue;
                float bx = left + i * step + step * 0.15f;
                float bw = Mathf.Max(1f, step * 0.7f);
                UPanel(c, box, null, pvp ? ColSegPvp : ColAccent, bx, bottom - barH, bw, barH);
                if (!pvp)
                {
                    int failed = bucket.CaptureFailures + bucket.CaptureManualCancels;
                    if (bucket.CaptureSuccesses > 0)
                    {
                        float sh = plotH * bucket.CaptureSuccesses / maxValue;
                        UPanel(c, box, null, ColSegHold, bx, bottom - sh, bw / 2f, sh);
                    }
                    if (failed > 0)
                    {
                        float fh = plotH * failed / maxValue;
                        UPanel(c, box, null, ColRedTimer, bx + bw / 2f, bottom - fh, bw / 2f, fh);
                    }
                }
            }

            UText(c, box, "−" + hours + " ч", 9, ColTextMute, TextAnchor.MiddleLeft, left, bottom + 4f, 60, 16);
            UText(c, box, "сейчас", 9, ColTextMute, TextAnchor.MiddleRight, right - 60f, bottom + 4f, 60, 16);
            if (!pvp)
                UText(c, box, "<color=#cd5432>начато</color>  <color=#73b34d>успешно</color>  <color=#ff5445>сорвано</color>", 9, ColTextMute, TextAnchor.MiddleCenter, left + 60f, bottom + 4f, right - left - 120f, 16);
        }

        private void DrawAdminHelp(CuiElementContainer c)
        {
            string p = AdminDynamic;
            DrawTitle(c, p, "СПРАВКА АДМИНИСТРАТОРА", "Конфиг: oxide/config/RockRustClans.json   •   Данные: oxide/data/RockRustClans.json");
            string body =
                "<size=17><b>БЫСТРЫЙ СТАРТ</b></size>\n" +
                "1. Вкладка <b>ТЕРРИТОРИИ</b>: нажмите ТЕЛЕПОРТ у нужного РТ, встаньте туда, где должен стоять генератор, и нажмите <b>ТОЧКА ЗДЕСЬ</b>. Точка хранится относительно монумента, поэтому переживает вайп и смену карты.\n" +
                "2. Протестируйте систему сами (пока она закрыта, админы видят её в режиме теста).\n" +
                "3. Вкладка <b>НАСТРОЙКИ</b> → «Система открыта для всех игроков» → ВКЛ. Перезагрузка плагина не нужна.\n\n" +
                "<size=17><b>КЛАНЫ</b></size>\n" +
                "Поиск слева — по тегу, названию или нику участника. В карточке клана: новое название и тег (для админа без ограничения «раз за вайп»), очки кнопками или вводом (<b>+50</b>, <b>-20</b>, <b>=300</b>), смена главы, заместитель, роль, кик и добавление игроков онлайн. Все изменения очков пишутся в журнал со строкой АДМИН и показываются игрокам отдельной строкой «Администратор».\n\n" +
                "<size=17><b>ЗЕЛЁНКА</b></size>\n" +
                "Привязка текущей команды главы, приглашение, принудительное добавление онлайн-игрока, удаление из команды, возврат лидерства главе и отвязка. Отвязка не распускает саму команду Rust.\n\n" +
                "<size=17><b>ТЕРРИТОРИИ</b></size>\n" +
                "ТОЧКА ЗДЕСЬ — сохранить точку генератора. ТЕЛЕПОРТ — к точке или центру РТ. ВКЛ/ВЫКЛ — убрать РТ из игры. СНЯТЬ КД, ОТМЕНИТЬ ЗАХВАТ, СБРОС ВЛАДЕЛЬЦА. ОТДАТЬ — передать РТ клану, выбранному во вкладке КЛАНЫ.\n\n" +
                "<size=17><b>КАК СЧИТАЮТСЯ ОЧКИ</b></size>\n" +
                "Убийство участника другого клана — очки клану убийцы (повтор той же жертвы — после защиты от фарма). Убийство считается в PvP даже при 0 очков за него. Захват — очки за захват, плюс бонус, если РТ отбит у другого клана. Удержание — каждые N минут, если на РТ есть нужное число участников. Пока РТ захватывает другой клан, удержание на паузе (настраивается).\n\n" +
                "<size=17><b>КОМАНДЫ</b></size>\n" +
                "<b>/clanadmin</b> — панель.  <b>/clanadmin setpoint ID</b> — точка РТ.  <b>/clanadmin score TAG ±N</b> — очки.  <b>/clanadmin reset</b> — сброс сезона.  <b>/clanadmin status</b> — состояние РТ в чат.  <b>/clanadmin cleanupstation</b> — удалить старые мешки рядом.\n" +
                "Право: <b>rockrustclans.admin</b> (или админ сервера).";
            string box = p + ".HelpBox";
            float viewY = 62f;
            float viewH = CH - viewY;
            UPanel(c, p, box, ColPanel, 0, viewY, CW, viewH, SpriteTile);
            int lines = body.Split('\n').Length + body.Length / 95;
            float contentH = Mathf.Max(viewH, 40f + lines * 19f);
            string scroll = box + ".Scroll";
            UScroll(c, box, scroll, 0, 0, CW, viewH, contentH);
            UPanel(c, scroll, null, ColAccent, 16, 18, 3, contentH - 36f);
            UText(c, scroll, body, 13, ColText, TextAnchor.UpperLeft, 32, 14, CW - 64f, contentH - 28f);
        }

        #endregion

        #region GUI: HUD захвата

        private readonly HashSet<ulong> _hudVisible = new HashSet<ulong>();

        private void UpdateCaptureHudForAll()
        {
            List<RuntimeTerritory> active = _runtimeTerritories.Values
                .Where(t => !string.IsNullOrEmpty(t.State.CapturingTag))
                .OrderBy(t => t.Config.DisplayName)
                .Take(3)
                .ToList();

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected)
                    continue;
                UpdateCaptureHud(player, active);
            }
        }

        private void UpdateCaptureHud(BasePlayer player, List<RuntimeTerritory> active)
        {
            if (player == null) return;
            ulong userId = player.userID;
            bool show = active.Count > 0 && player.IsConnected && CanAccessSystem(player);
            if (!show)
            {
                // Не шлём пустые команды каждые 10 секунд, если HUD и так скрыт.
                if (_hudVisible.Remove(userId))
                    CuiHelper.DestroyUi(player, HudRoot);
                return;
            }

            CuiHelper.DestroyUi(player, HudRoot);
            _hudVisible.Add(userId);

            ClanData own = GetClan(player.userID);
            float rowH = 46f, gap = 4f, width = 340f;
            float total = active.Count * rowH + (active.Count - 1) * gap;

            CuiElementContainer c = new CuiElementContainer();
            c.Add(new CuiElement
            {
                Name = HudRoot,
                Parent = "Hud",
                Components =
                {
                    new CuiImageComponent { Color = "0 0 0 0" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = F(-width / 2f) + " " + F(-(10f + total)), OffsetMax = F(width / 2f) + " -10" }
                }
            });

            for (int i = 0; i < active.Count; i++)
            {
                RuntimeTerritory t = active[i];
                bool mine = own != null && string.Equals(own.Tag, t.State.CapturingTag, StringComparison.OrdinalIgnoreCase);
                bool defending = own != null && string.Equals(own.Tag, t.State.OwnerTag, StringComparison.OrdinalIgnoreCase) && !mine;
                string row = HudRoot + ".R" + i;
                UPanel(c, HudRoot, row, "0.106 0.106 0.098 0.90", 0, i * (rowH + gap), width, rowH, SpriteTile);
                UPanel(c, row, null, mine ? ColSegHold : defending ? ColRedTimer : ColAccent, 0, 0, 3, rowH);
                UIcon(c, row, TerritoryIcon(t.Config), 8, 7, 32);
                UText(c, row, string.Format("{0}  <color={1}>[{2}]</color>", EscapeRichText(t.Config.DisplayName.ToUpperInvariant()), TagColor, t.State.CapturingTag), 12, ColText, TextAnchor.MiddleLeft, 48, 3, 180, 20, true);
                int remaining = Mathf.Max(0, Mathf.CeilToInt(_config.Capture.CaptureDurationSeconds - t.State.ProgressSeconds));
                UText(c, row, GetCaptureStatusShort(t) + "  " + FormatDuration(remaining), 11, ColText, TextAnchor.MiddleRight, width - 170f, 3, 160, 20);
                DrawBar(c, row, 48, 28, width - 100f, 8, CaptureRatio(t.State), mine ? ColSegHold : ColAccent);
                UText(c, row, CapturePercent(t.State) + "%", 11, ColText, TextAnchor.MiddleRight, width - 48f, 22, 38, 20, true);
            }

            CuiHelper.AddUi(player, c);
        }

        private string GetCaptureStatusShort(RuntimeTerritory territory)
        {
            switch (territory.State.Status)
            {
                case "contested": return "<color=#ff6b5e>ОСПАРИВАЕТСЯ</color>";
                case "takeover_ready": return "<color=#ffb35a>МОЖНО ПЕРЕХВАТИТЬ</color>";
                case "waiting_fuel": return "<color=#efc34d>НЕТ ТОПЛИВА</color>";
                case "no_defenders": return "<color=#efc34d>НЕТ ЗАЩИТНИКОВ</color>";
                case "capturing": return "<color=#9fd36b>ИДЁТ ЗАХВАТ</color>";
                default: return "ОЖИДАНИЕ";
            }
        }

        #endregion

        #region GUI: базовые элементы

        private string TagColor
        {
            get { return _config != null && _config.Chat != null && !string.IsNullOrEmpty(_config.Chat.TagColor) ? _config.Chat.TagColor : "#d6a84b"; }
        }

        private bool IsOnline(ulong id)
        {
            BasePlayer player = BasePlayer.FindByID(id);
            return player != null && player.IsConnected;
        }

        private float CaptureRatio(TerritoryState state)
        {
            if (state == null || _config.Capture.CaptureDurationSeconds <= 0)
                return 0f;
            return Mathf.Clamp01(state.ProgressSeconds / _config.Capture.CaptureDurationSeconds);
        }

        // Округляем вниз, чтобы «100%» не показывалось раньше реального завершения захвата.
        private int CapturePercent(TerritoryState state)
        {
            return Mathf.FloorToInt(CaptureRatio(state) * 100f);
        }

        private string RankColor(int rank)
        {
            if (rank == 1) return ColGold;
            if (rank == 2) return ColSilver;
            if (rank == 3) return ColBronze;
            return ColText;
        }

        private string DefaultTerritoryIcon(string id)
        {
            string key = (id ?? string.Empty).ToLowerInvariant();
            if (key.Contains("launch")) return "rocket.launcher";
            if (key.Contains("airfield")) return "supply.signal";
            if (key.Contains("airport")) return "map";
            return "electric.fuelgenerator.small";
        }

        private string[] TerritoryIcon(TerritoryConfig config)
        {
            return new[] { config == null ? null : config.IconShortname, config == null ? null : DefaultTerritoryIcon(config.Id), "electric.fuelgenerator.small" };
        }

        private int ItemIdOf(string[] shortnames)
        {
            if (shortnames == null)
                return 0;
            foreach (string shortname in shortnames)
            {
                if (string.IsNullOrEmpty(shortname))
                    continue;
                int id;
                if (!_itemIdCache.TryGetValue(shortname, out id))
                {
                    ItemDefinition definition = ItemManager.FindItemDefinition(shortname);
                    id = definition == null ? 0 : definition.itemid;
                    _itemIdCache[shortname] = id;
                }
                if (id != 0)
                    return id;
            }
            return 0;
        }

        private CuiRectTransformComponent TL(float x, float y, float w, float h)
        {
            return new CuiRectTransformComponent
            {
                AnchorMin = "0 1",
                AnchorMax = "0 1",
                OffsetMin = F(x) + " " + F(-(y + h)),
                OffsetMax = F(x + w) + " " + F(-y)
            };
        }

        private void UPanel(CuiElementContainer c, string parent, string name, string color, float x, float y, float w, float h, string sprite = null)
        {
            CuiImageComponent image = new CuiImageComponent { Color = color };
            if (!string.IsNullOrEmpty(sprite))
                image.Sprite = sprite;
            c.Add(new CuiElement
            {
                Name = name ?? CuiHelper.GetGuid(),
                Parent = parent,
                Components = { image, TL(x, y, w, h) }
            });
        }

        private void UFrame(CuiElementContainer c, string parent, string color, float x, float y, float w, float h)
        {
            UPanel(c, parent, null, color, x, y, w, 1);
            UPanel(c, parent, null, color, x, y + h - 1f, w, 1);
            UPanel(c, parent, null, color, x, y, 1, h);
            UPanel(c, parent, null, color, x + w - 1f, y, 1, h);
        }

        private void UText(CuiElementContainer c, string parent, string text, int size, string color, TextAnchor align, float x, float y, float w, float h, bool bold = false)
        {
            c.Add(new CuiElement
            {
                Name = CuiHelper.GetGuid(),
                Parent = parent,
                Components =
                {
                    new CuiTextComponent { Text = text ?? string.Empty, FontSize = size, Font = bold ? FontBold : FontReg, Align = align, Color = color },
                    TL(x, y, w, h)
                }
            });
        }

        private void UButton(CuiElementContainer c, string parent, string command, string color, string text, int size, string textColor, float x, float y, float w, float h, string name = null)
        {
            c.Add(new CuiButton
            {
                Button = { Command = command ?? string.Empty, Color = color },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = F(x) + " " + F(-(y + h)), OffsetMax = F(x + w) + " " + F(-y) },
                Text = { Text = text ?? string.Empty, FontSize = size, Font = FontBold, Align = TextAnchor.MiddleCenter, Color = textColor }
            }, parent, name ?? CuiHelper.GetGuid());
        }

        private void UCloseButton(CuiElementContainer c, string parent, string command, float x, float y, float size = 40f)
        {
            string name = CuiHelper.GetGuid();
            UButton(c, parent, command, ColRedBtn, string.Empty, 1, ColRedTxt, x, y, size, size, name);
            float pad = Mathf.Round(size * 0.3f);
            UPanel(c, name, null, ColRedTxt, pad, pad, size - pad * 2f, size - pad * 2f, IconClose);
        }

        private void UIcon(CuiElementContainer c, string parent, string[] shortnames, float x, float y, float size)
        {
            int itemId = ItemIdOf(shortnames);
            if (itemId == 0)
                return;
            c.Add(new CuiElement
            {
                Name = CuiHelper.GetGuid(),
                Parent = parent,
                Components =
                {
                    new CuiImageComponent { ItemId = itemId },
                    TL(x, y, size, size)
                }
            });
        }

        private void UAvatar(CuiElementContainer c, string parent, ulong steamId, float x, float y, float size)
        {
            UPanel(c, parent, null, ColBorder, x, y, size, size);
            if (steamId == 0UL)
            {
                UIcon(c, parent, IcoMembers, x + 2f, y + 2f, size - 4f);
                return;
            }
            c.Add(new CuiElement
            {
                Name = CuiHelper.GetGuid(),
                Parent = parent,
                Components =
                {
                    new CuiRawImageComponent { SteamId = steamId.ToString(), Color = "1 1 1 1" },
                    TL(x + 1f, y + 1f, size - 2f, size - 2f)
                }
            });
        }

        private void UScroll(CuiElementContainer c, string parent, string name, float x, float y, float w, float h, float contentH)
        {
            contentH = Mathf.Max(h, contentH);
            c.Add(new CuiElement
            {
                Name = name,
                Parent = parent,
                Components =
                {
                    new CuiScrollViewComponent
                    {
                        Vertical = true,
                        Horizontal = false,
                        MovementType = UnityEngine.UI.ScrollRect.MovementType.Clamped,
                        Elasticity = 0.25f,
                        Inertia = true,
                        DecelerationRate = 0.3f,
                        ScrollSensitivity = 28f,
                        ContentTransform = new CuiRectTransform { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -" + F(contentH), OffsetMax = "0 0" },
                        VerticalScrollbar = new CuiScrollbar
                        {
                            Size = 6f,
                            AutoHide = true,
                            HandleSprite = SpriteTile,
                            TrackSprite = SpriteTile,
                            HandleColor = "0.804 0.329 0.196 0.85",
                            HighlightColor = "0.9 0.42 0.28 1",
                            PressedColor = "1 0.5 0.35 1",
                            TrackColor = "0.18 0.17 0.155 0.8"
                        }
                    },
                    TL(x, y, w, h)
                }
            });
        }

        private void DrawInput(CuiElementContainer c, string parent, string name, string command, int limit, float x, float y, float w, float h)
        {
            string bg = name + ".Bg";
            UPanel(c, parent, bg, ColTrack, x, y, w, h);
            UPanel(c, bg, null, ColAccent, 0, h - 2f, w, 2);
            c.Add(new CuiElement
            {
                Name = name,
                Parent = bg,
                Components =
                {
                    new CuiInputFieldComponent { Command = command, CharsLimit = limit, FontSize = 13, Font = FontReg, Align = TextAnchor.MiddleLeft, Color = ColText },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "10 2", OffsetMax = "-10 0" },
                    new CuiNeedsKeyboardComponent()
                }
            });
        }

        private void DrawTitle(CuiElementContainer c, string parent, string title, string subtitle)
        {
            UText(c, parent, title, 18, ColText, TextAnchor.MiddleLeft, 0, 0, CW * 0.6f, 28, true);
            if (!string.IsNullOrEmpty(subtitle))
                UText(c, parent, subtitle, 11, ColTextDim, TextAnchor.MiddleLeft, 0, 27, CW, 18);
            UPanel(c, parent, null, ColBorder, 0, 50, CW, 1);
        }

        private void DrawStatCard(CuiElementContainer c, string parent, string key, string[] icon, string label, string value, string valueColor, string info, float x, float y, float w, float h)
        {
            string name = parent + ".Stat." + key;
            UPanel(c, parent, name, ColPanel, x, y, w, h, SpriteTile);
            UPanel(c, name, null, ColPanelDark, 10, (h - 56f) / 2f, 56, 56, SpriteCircle);
            UIcon(c, name, icon, 18, (h - 40f) / 2f, 40);
            UText(c, name, label, 10, ColTextDim, TextAnchor.LowerLeft, 76, 10, w - 110f, 20, true);
            UText(c, name, value, 22, valueColor ?? ColText, TextAnchor.UpperLeft, 76, 32, w - 84f, 34, true);
            if (!string.IsNullOrEmpty(info))
                UButton(c, name, "rrclan.info " + info, ColGrayBtn, "?", 12, ColTextDim, w - 30f, 8, 22, 22);
        }

        private void DrawBar(CuiElementContainer c, string parent, float x, float y, float w, float h, float ratio, string color)
        {
            UPanel(c, parent, null, ColTrack, x, y, w, h);
            ratio = Mathf.Clamp01(ratio);
            if (ratio > 0f)
                UPanel(c, parent, null, color, x, y, Mathf.Max(2f, w * ratio), h);
        }

        private void DrawChip(CuiElementContainer c, string parent, string text, string bg, string fg, float x, float y, float w, float h)
        {
            UPanel(c, parent, null, bg, x, y, w, h);
            UText(c, parent, text, 10, fg, TextAnchor.MiddleCenter, x, y, w, h, true);
        }

        private string FormatDate(long unix)
        {
            if (unix <= 0)
                return "—";
            return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        }

        private string FormatMinutes(int seconds)
        {
            if (seconds <= 0)
                return "0 сек";
            if (seconds < 60)
                return seconds + " сек";
            int minutes = seconds / 60;
            int rest = seconds % 60;
            if (minutes >= 60)
            {
                int hours = minutes / 60;
                int mins = minutes % 60;
                return mins == 0 ? hours + " ч" : hours + " ч " + mins + " мин";
            }
            return rest == 0 ? minutes + " мин" : minutes + " мин " + rest + " сек";
        }

        #endregion

        #endregion

        #region Helpers

        private void BeginPendingChatInput(BasePlayer player, string action, ClanData clan)
        {
            if (player == null || !CanAccessSystem(player) || string.IsNullOrEmpty(action))
                return;

            _pendingChatInputs[player.userID] = new PendingChatInput
            {
                Action = action,
                ClanId = clan == null ? string.Empty : clan.ClanId,
                ExpiresUnix = UnixNow() + 90
            };

            CuiHelper.DestroyUi(player, UiRoot);
            CuiHelper.DestroyUi(player, AdminUiRoot);
            _clanUiOpen.Remove(player.userID);
            _adminUiOpen.Remove(player.userID);

            string prompt;
            switch (action)
            {
                case "create":
                    prompt = string.Format("Введите TAG нового клана в чат ({0}–{1} символов A-Z/0-9). Текст не увидят другие игроки. Для отмены: <color=#d6a84b>/clan cancel</color>", _config.Clan.MinTagLength, _config.Clan.MaxTagLength);
                    break;
                case "leader_rename":
                case "admin_rename":
                    prompt = "Введите новое название клана в чат (2–32 символа). Текст не увидят другие игроки. Для отмены: <color=#d6a84b>/clan cancel</color>";
                    break;
                case "leader_retag":
                case "admin_retag":
                    prompt = string.Format("Введите новый TAG в чат ({0}–{1} символов A-Z/0-9). Текст не увидят другие игроки. Для отмены: <color=#d6a84b>/clan cancel</color>", _config.Clan.MinTagLength, _config.Clan.MaxTagLength);
                    break;
                default:
                    _pendingChatInputs.Remove(player.userID);
                    return;
            }

            SendReply(player, "<color=#d6a84b>[КЛАНЫ]</color> " + prompt);
        }

        private void HandlePendingChatInput(BasePlayer player, PendingChatInput pending, string value)
        {
            if (player == null || !CanAccessSystem(player) || pending == null)
                return;

            if (pending.Action == "create")
            {
                CreateClan(player, value);
                NextTick(() => { if (player != null && player.IsConnected) OpenClanUi(player, "main"); });
                return;
            }

            ClanData clan = GetClanById(pending.ClanId);
            if (clan == null)
            {
                SendReply(player, "Клан больше не существует.");
                return;
            }

            if (pending.Action == "leader_rename" || pending.Action == "leader_retag")
            {
                ClanData ownClan = GetClan(player.userID);
                if (ownClan == null || ownClan.ClanId != clan.ClanId || !IsLeader(clan, player.userID))
                {
                    SendReply(player, "Это действие доступно только текущему главе клана.");
                    return;
                }

                if (pending.Action == "leader_rename")
                    ChangeClanName(clan, value, player, false);
                else
                    ChangeClanTag(clan, value, player, false);

                NextTick(() => { if (player != null && player.IsConnected) OpenClanUi(player, "manage"); });
                return;
            }

            if (pending.Action == "admin_rename" || pending.Action == "admin_retag")
            {
                if (!HasAdminPermission(player))
                {
                    SendReply(player, "Нет прав.");
                    return;
                }

                if (pending.Action == "admin_rename")
                    ChangeClanName(clan, value, player, true);
                else
                    ChangeClanTag(clan, value, player, true);

                string currentTag = clan.Tag;
                NextTick(() => { if (player != null && player.IsConnected) OpenAdminUi(player, currentTag, "clans"); });
            }
        }

        private ClanData GetClanById(string clanId)
        {
            if (string.IsNullOrEmpty(clanId))
                return null;

            return _data.Clans.Values.FirstOrDefault(c => c != null && string.Equals(c.ClanId, clanId, StringComparison.Ordinal));
        }

        private string GetConsoleText(ConsoleSystem.Arg arg, int startIndex)
        {
            if (arg == null || arg.Args == null || startIndex < 0 || startIndex >= arg.Args.Length) return string.Empty;
            List<string> parts = new List<string>();
            for (int i = startIndex; i < arg.Args.Length; i++)
            {
                string part = arg.GetString(i, string.Empty);
                if (!string.IsNullOrEmpty(part)) parts.Add(part);
            }
            return string.Join(" ", parts.ToArray()).Trim();
        }

        private BasePlayer FindOnlinePlayer(string query, BasePlayer requester)
        {
            if (string.IsNullOrWhiteSpace(query))
                return null;

            query = query.Trim();
            ulong id;
            if (ulong.TryParse(query, out id))
            {
                BasePlayer byId = BasePlayer.FindByID(id);
                if (byId != null && byId.IsConnected)
                    return byId;
            }

            List<BasePlayer> matches = BasePlayer.activePlayerList
                .Where(p => p != null && p.displayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (matches.Count == 1)
                return matches[0];

            if (requester != null)
                SendReply(requester, matches.Count == 0 ? "Игрок не найден." : "Найдено несколько игроков — укажите имя точнее.");
            return null;
        }

        private ulong FindPlayerIdInClan(ClanData clan, string query)
        {
            if (clan == null || string.IsNullOrWhiteSpace(query)) return 0UL;
            ulong id;
            if (ulong.TryParse(query, out id) && clan.Members.Contains(id))
                return id;

            List<ulong> matches = clan.Members.Where(member => GetKnownName(clan, member).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            return matches.Count == 1 ? matches[0] : 0UL;
        }

        private string GetKnownName(ClanData clan, ulong id)
        {
            BasePlayer online = BasePlayer.FindByID(id);
            if (online != null && !string.IsNullOrEmpty(online.displayName))
                return online.displayName;

            string name;
            if (clan != null && clan.Names != null && clan.Names.TryGetValue(id, out name) && !string.IsNullOrEmpty(name))
                return name;
            return id.ToString();
        }

        private void BroadcastToClan(ClanData clan, string message)
        {
            if (clan == null) return;
            string line = string.Format("<color={0}>[КЛАН {1}]</color> {2}", _config.Chat.ClanChatColor, clan.Tag, message);
            foreach (ulong id in clan.Members)
            {
                BasePlayer player = BasePlayer.FindByID(id);
                if (player != null && player.IsConnected && CanAccessSystem(player))
                    PrintToChat(player, line);
            }
        }

        private void CleanupInvites()
        {
            long now = UnixNow();
            bool changed = false;
            List<ulong> expired = _data.Invites.Where(x => x.Value == null || x.Value.ExpiresUnix < now).Select(x => x.Key).ToList();
            foreach (ulong id in expired)
            {
                _data.Invites.Remove(id);
                changed = true;
            }

            long cooldownLifetime = Math.Max(60, _config.Score.SameVictimCooldownSeconds + 60);
            List<string> oldKillCooldowns = _data.KillRewardCooldowns.Where(x => now - x.Value > cooldownLifetime).Select(x => x.Key).ToList();
            foreach (string key in oldKillCooldowns)
            {
                _data.KillRewardCooldowns.Remove(key);
                changed = true;
            }

            if (changed) SaveData();
        }

        private string NormalizeTag(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return Regex.Replace(value.Trim().ToUpperInvariant(), "[^A-Z0-9]", string.Empty);
        }

        private bool IsPublicSystemEnabled
        {
            get { return _config != null && _config.Access != null && _config.Access.EnableForRegularPlayers; }
        }

        private bool CanAccessSystem(BasePlayer player)
        {
            if (player == null)
                return false;
            if (IsPublicSystemEnabled)
                return true;
            return _config != null && _config.Access != null && _config.Access.AllowAdministratorTesting && HasAdminPermission(player);
        }

        private bool CanAccessSystem(ulong userId)
        {
            if (IsPublicSystemEnabled)
                return true;
            if (_config == null || _config.Access == null || !_config.Access.AllowAdministratorTesting || userId == 0UL)
                return false;

            BasePlayer player = BasePlayer.FindByID(userId);
            if (player != null)
                return HasAdminPermission(player);

            return permission.UserHasPermission(userId.ToString(), PermissionAdmin);
        }

        private void AnnounceSystem(string message)
        {
            if (string.IsNullOrEmpty(message))
                return;

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player != null && player.IsConnected && CanAccessSystem(player))
                    PrintToChat(player, message);
            }
        }

        private bool HasAdminPermission(BasePlayer player)
        {
            return player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionAdmin));
        }

        private long UnixNow()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private string FormatDuration(long seconds)
        {
            if (seconds < 0) seconds = 0;
            long minutes = seconds / 60;
            long secs = seconds % 60;
            if (minutes >= 60)
                return string.Format("{0}:{1:00}:{2:00}", minutes / 60, minutes % 60, secs);
            return string.Format("{0}:{1:00}", minutes, secs);
        }

        private string EscapeRichText(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("<", "‹").Replace(">", "›");
        }

        private string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
            return value.Substring(0, Math.Max(1, max - 1)) + "…";
        }

        private string FormatVector(Vector3 value)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0},{1:0.0},{2:0.0}", value.x, value.y, value.z);
        }

        private string F(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        #endregion

        #region Public API

        private string API_GetClanTag(ulong playerId)
        {
            ClanData clan = GetClan(playerId);
            return clan == null ? null : clan.Tag;
        }

        private bool API_AreClanMates(ulong first, ulong second)
        {
            ClanData a = GetClan(first);
            ClanData b = GetClan(second);
            return a != null && b != null && string.Equals(a.Tag, b.Tag, StringComparison.OrdinalIgnoreCase);
        }

        private int API_GetClanScore(string tag)
        {
            ClanData clan = GetClanByTag(tag);
            return clan == null ? 0 : clan.Score;
        }

        #endregion
    }
}