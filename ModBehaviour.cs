// ModBehaviour.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ItemStatsSystem;
using UnityEngine;

namespace ItemReroll
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        // ── CFG (편집 가능) ───────────────────────────────────────
        private string _cfgPath; // <persistentDataPath>/ItemReroll.cfg
        private const string CFG_HEADER =
            "# ItemReroll config (editable)\n" +
            "# Keys:\n" +
            "#   RerollKey=F9           ; 리롤 실행 키 (Unity KeyCode)\n" +
            "#   CostStep=1000           ; 리롤 성공 시 증가 비용(선형)\n" +
            "#   UseCost=1              ; 1=비용 사용, 0=비용 미사용(디버그)\n" +
            "#   DebugCurrency=0        ; 1=통화 바인딩 상세 로그 출력\n" +
            "#   CurrencyHintType=      ; (선택) 통화 컴포넌트/매니저 타입명\n" +
            "#   CurrencyHintMember=    ; (선택) 돈 필드/프로퍼티명\n" +
            "#   CurrencyGameObject=    ; (선택) 돈이 붙은 GO 이름(부분일치)\n" +
            "#   CurrencyMethodGet=     ; (선택) 돈 읽기 메서드명(int/float)\n" +
            "#   CurrencyMethodSet=     ; (선택) 돈 설정 메서드명(int/float 1개 인자)\n" +
            "#   CurrencyMethodAdd=     ; (선택) 돈 증감 메서드명(int/float 1개 인자, -지출)\n";
        // ─────────────────────────────────────────────────────────

        private const string LOG_PREFIX = "[ItemReroll]";

        // 리롤 키 및 리바인 (Insert로 변경)
        private KeyCode _rerollKey = KeyCode.F9;
        private const string PREFS_REROLL_KEY = "ItemReroll.RerollKey";
        private bool _waitingRebind = false;

        // 비용(RefreshStockPrice 기반)
        private const string PREFS_COST = "ItemReroll.RerollCost";
        private long _rerollCostBase = 100;      // LuckyBox 없을 때 백업 기본값
        private long _rerollCostStep = 1000;       // 성공 시 증가 폭 (CFG)
        private long _rerollCostCurrent = 100;   // 현재 비용
        private bool _useCost = true;

        // LuckyBox SettingManager 캐시
        private object _lbSettingMgr;      // DuckovLuckyBox.Core.Settings.SettingManager.Instance
        private object _lbRefreshSetting;  // SettingItem: RefreshStockPrice
        private MethodInfo _refreshGetAsLong; // SettingItem.GetAsLong (이벤트 핸들러에서 사용)

        // 소비 텍스트 HUD (가벼운 OnGUI)
        private int _lastSpentAmount = 0;
        private float _lastSpendShowUntil = 0f;

        // 통화 브릿지(힌트/휴리스틱)
        private readonly CurrencyBridge _currency = new CurrencyBridge();

        private readonly ItemDatabase _itemDatabase = new ItemDatabase();
        private readonly ItemFilter _itemFilter = new ItemFilter();
        private readonly ItemReroller _itemReroller = new ItemReroller();
        private bool _isRerolling;

        private void Awake()
        {
            LogSection("모드 초기화 시작");

            _itemDatabase.LoadFromGame();

            _cfgPath = UnityEngine.Application.persistentDataPath + "/ItemReroll.cfg";
            LoadConfig();        // CFG (키/증가폭/통화힌트/UseCost/Debug)
            LoadRerollKey();     // PlayerPrefs 보조

            // LuckyBox의 RefreshStockPrice를 리롤 기본 비용으로 사용 (없으면 백업값 유지)
            TryLoadPriceFromLuckyBox();

            // 현재 비용 초기화 (PlayerPrefs → base 최소 보정)
            _rerollCostCurrent = Math.Max(_rerollCostBase, PlayerPrefs.GetInt(PREFS_COST, (int)_rerollCostBase));
            PlayerPrefs.SetInt(PREFS_COST, (int)_rerollCostCurrent);
            PlayerPrefs.Save();

            _currency.WarmUp();

            Debug.Log($"{LOG_PREFIX} 유효한 아이템 ID: {_itemDatabase.Count}개");
            if (_itemDatabase.Count > 0)
                Debug.Log($"{LOG_PREFIX} ID 범위: {_itemDatabase.MinID} ~ {_itemDatabase.MaxID}");

            Debug.Log($"{LOG_PREFIX} 모드 로드 완료!");
            LogSection("");
        }

        private void Update()
        {
            // ── 키 리바인 토글: Insert ───────────────────────────────
            if (!_waitingRebind && Input.GetKeyDown(KeyCode.Insert))
            {
                _waitingRebind = true;
                Debug.Log("[ItemReroll] 키 변경 대기... 아무 키나 누르면 저장 (Esc로 취소)");
            }
            if (_waitingRebind)
            {
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    _waitingRebind = false;
                    Debug.Log("[ItemReroll] 키 변경 취소");
                }
                else
                {
                    foreach (KeyCode kc in Enum.GetValues(typeof(KeyCode)))
                    {
                        if (kc == KeyCode.None || kc == KeyCode.Escape || kc == KeyCode.Insert) continue;
                        if (Input.GetKeyDown(kc))
                        {
                            _waitingRebind = false;
                            SetRerollKey(kc.ToString());
                            break;
                        }
                    }
                }
            }
            // ─────────────────────────────────────────────────────────

            // 리롤 실행
            if (Input.GetKeyDown(_rerollKey))
            {
                if (_isRerolling)
                {
                    Debug.LogWarning($"{LOG_PREFIX} 이미 리롤이 진행 중입니다. 대기 중...");
                    return;
                }
                LogSection($"{_rerollKey} 키 입력 감지! 리롤 시작...");
                StartCoroutine(PerformReroll());
            }
        }

        private void OnGUI()
        {
            if (Time.time <= _lastSpendShowUntil && _lastSpentAmount > 0)
            {
                var r = new Rect(Screen.width - 220, 20, 200, 30);
                GUIStyle s = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleRight,
                    fontSize = 18,
                    fontStyle = FontStyle.Bold
                };
                GUI.Label(r, $"-{_lastSpentAmount}", s);
            }
        }

        private IEnumerator PerformReroll()
        {
            if (_isRerolling) yield break;
            _isRerolling = true;

            List<Item> containerItems = null;

            try
            {
                containerItems = FindContainerItems();
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LOG_PREFIX} 아이템 탐색 중 오류 발생!");
                Debug.LogError($"{LOG_PREFIX} 오류 메시지: {ex.Message}");
                Debug.LogError($"{LOG_PREFIX} 스택 트레이스: {ex.StackTrace}");
                _isRerolling = false;
                yield break;
            }

            if (containerItems == null || containerItems.Count == 0)
            {
                Debug.Log($"{LOG_PREFIX} 리롤할 컨테이너 아이템이 없습니다.");
                _isRerolling = false;
                yield break;
            }

            // ── 돈 확인/차감 (EconomyManager 우선 → 폴백) ─────────────
            if (_useCost)
            {
                long costNow = Math.Max(_rerollCostBase, _rerollCostCurrent);

                if (!TryPay(costNow))
                {
                    Debug.Log($"{LOG_PREFIX} 결제 실패 또는 잔액 부족. 필요:{costNow}");
                    _isRerolling = false;
                    yield break;
                }

                _lastSpentAmount = (int)costNow;
                _lastSpendShowUntil = Time.time + 2.5f;
                Debug.Log($"{LOG_PREFIX} 비용 차감: -{costNow}");
            }
            // ───────────────────────────────────────────────────────

            Debug.Log($"{LOG_PREFIX} [3단계] {containerItems.Count}개 컨테이너 아이템 리롤 시작...");

            int successCount = 0;
            int failCount = 0;

            for (int i = 0; i < containerItems.Count; i++)
            {
                bool success = _itemReroller.RerollItem(containerItems[i], _itemDatabase);
                if (success) successCount++; else failCount++;
                yield return null;
            }

            // 성공 시 다음 비용 증가
            if (_useCost && successCount > 0)
            {
                try
                {
                    checked { _rerollCostCurrent = Math.Max(_rerollCostBase, _rerollCostCurrent + _rerollCostStep); }
                }
                catch { _rerollCostCurrent = Math.Max(_rerollCostBase, int.MaxValue); }

                PlayerPrefs.SetInt(PREFS_COST, (int)_rerollCostCurrent);
                PlayerPrefs.Save();
                Debug.Log($"{LOG_PREFIX} 리롤 성공({successCount}). 다음 비용: {_rerollCostCurrent} (+{_rerollCostStep})");
            }

            ShowResults(successCount, failCount);

            _isRerolling = false;
            Debug.Log($"{LOG_PREFIX} 리롤 프로세스 완료. 다음 리롤 대기 중...");
        }

        private List<Item> FindContainerItems()
        {
            Item[] allItems = UnityEngine.Object.FindObjectsOfType<Item>();
            var filterResult = _itemFilter.FilterContainerItems(allItems);
            return filterResult.ContainerItems;
        }

        private void ShowResults(int successCount, int failCount)
        {
            Debug.Log($"{LOG_PREFIX} [4단계] 리롤 완료!");
            Debug.Log($"{LOG_PREFIX}   - 성공: {successCount}개");
            Debug.Log($"{LOG_PREFIX}   - 실패: {failCount}개");
        }

        private void LogSection(string message)
        {
            Debug.Log($"{LOG_PREFIX} ========================================");
            if (!string.IsNullOrEmpty(message))
            {
                Debug.Log($"{LOG_PREFIX} {message}");
                Debug.Log($"{LOG_PREFIX} ========================================");
            }
        }

        // ─────────── LuckyBox RefreshStockPrice 연동 ───────────
        private void TryLoadPriceFromLuckyBox()
        {
            try
            {
                // SettingManager
                var smType = FindTypeAnyAssembly("DuckovLuckyBox.Core.Settings.SettingManager") ??
                             Type.GetType("DuckovLuckyBox.Core.Settings.SettingManager, DuckovLuckyBox");
                if (smType == null) return;

                var instProp = smType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                _lbSettingMgr = instProp?.GetValue(null);
                if (_lbSettingMgr == null) return;

                // RefreshStockPrice
                var refreshProp = smType.GetProperty("RefreshStockPrice", BindingFlags.Public | BindingFlags.Instance);
                _lbRefreshSetting = refreshProp?.GetValue(_lbSettingMgr);
                if (_lbRefreshSetting == null) return;

                var getAsLong = _lbRefreshSetting.GetType().GetMethod("GetAsLong", BindingFlags.Public | BindingFlags.Instance);
                if (getAsLong == null) return;

                var baseCost = (long)getAsLong.Invoke(_lbRefreshSetting, null);
                if (baseCost > 0)
                {
                    _rerollCostBase = baseCost;
                    _rerollCostCurrent = Math.Max(_rerollCostCurrent, _rerollCostBase);
                    Debug.Log($"[ItemReroll] LuckyBox RefreshStockPrice 적용: base={_rerollCostBase}, current={_rerollCostCurrent}");
                }

                // 값 변경 자동 반영(가능 시) — 로컬 함수 대신 인스턴스 메서드로 안전하게 구독
                var evt = _lbRefreshSetting.GetType().GetEvent("OnChanged", BindingFlags.Public | BindingFlags.Instance);
                if (evt != null)
                {
                    _refreshGetAsLong = getAsLong; // 핸들러에서 사용할 메서드 캐시
                    TrySubscribeRefreshChanged(_lbRefreshSetting, evt);
                }
            }
            catch { /* ignore */ }
        }

        // 이벤트 구독(시그니처가 EventHandler가 아닐 수도 있으니 CreateDelegate를 느슨하게 시도)
        private void TrySubscribeRefreshChanged(object setting, EventInfo evt)
        {
            try
            {
                var mi = typeof(ModBehaviour).GetMethod(nameof(OnRefreshChanged), BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null)
                         ?? typeof(ModBehaviour).GetMethod(nameof(OnRefreshChanged), BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(object), typeof(EventArgs) }, null);
                if (mi == null) return;

                var del = Delegate.CreateDelegate(evt.EventHandlerType, this, mi, throwOnBindFailure: false);
                if (del != null)
                {
                    evt.AddEventHandler(setting, del);
                    Debug.Log("[ItemReroll] RefreshStockPrice.OnChanged 구독 성공");
                }
                else
                {
                    Debug.Log("[ItemReroll] RefreshStockPrice.OnChanged 델리게이트 바인딩 실패 (시그니처 불일치 가능)");
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[ItemReroll] OnChanged 구독 실패: {ex.Message}");
            }
        }

        // 이벤트 핸들러(매개변수 0 버전)
        private void OnRefreshChanged()
        {
            ApplyRefreshPriceFromSetting();
        }

        // 이벤트 핸들러(매개변수 2 버전: object, EventArgs)
        private void OnRefreshChanged(object sender, EventArgs e)
        {
            ApplyRefreshPriceFromSetting();
        }

        private void ApplyRefreshPriceFromSetting()
        {
            try
            {
                if (_lbRefreshSetting != null && _refreshGetAsLong != null)
                {
                    var v = (long)_refreshGetAsLong.Invoke(_lbRefreshSetting, null);
                    if (v > 0)
                    {
                        _rerollCostBase = v;
                        if (_rerollCostCurrent < _rerollCostBase)
                            _rerollCostCurrent = _rerollCostBase;

                        Debug.Log($"[ItemReroll] RefreshStockPrice 변경 반영: base={_rerollCostBase}, current={_rerollCostCurrent}");
                    }
                }
            }
            catch { /* ignore */ }
        }

        // EconomyManager.Pay → 실패 시 CurrencyBridge 폴백
        private bool TryPay(long amount)
        {
            if (amount <= 0) return true;

            // 1) EconomyManager.Pay(Cost, bool, bool)
            try
            {
                var econType = FindTypeAnyAssembly("Duckov.Economy.EconomyManager") ??
                               Type.GetType("Duckov.Economy.EconomyManager, DuckovLuckyBox");
                var costType = FindTypeAnyAssembly("Duckov.Economy.Cost") ??
                               Type.GetType("Duckov.Economy.Cost, DuckovLuckyBox");

                if (econType != null && costType != null)
                {
                    var cost = Activator.CreateInstance(costType, new object[] { amount });
                    var pay = econType.GetMethod("Pay", BindingFlags.Public | BindingFlags.Static);
                    if (pay != null)
                    {
                        bool ok = (bool)pay.Invoke(null, new object[] { cost, true, true });
                        if (!ok) PushNotEnoughMoneyToast(amount); // 토스트
                        return ok;
                    }
                }
            }
            catch
            {
                // 폴백으로
            }

            // 2) 폴백: CurrencyBridge
            if (!_currency.TryGetMoney(out var money)) return false;
            if (money < amount) { PushNotEnoughMoneyToast(amount); return false; }
            if (_currency.TrySpendMoney((int)amount)) return true;
            return _currency.TrySetMoney(money - (int)amount);
        }

        private void PushNotEnoughMoneyToast(long amount)
        {
            try
            {
                // Localization: SodaCraft.Localizations.LocalizationManager.ToPlainText(Localizations.I18n.NotEnoughMoneyFormatKey)
                string message = null;

                var i18nType = FindTypeAnyAssembly("SodaCraft.Localizations.Localizations+I18n");
                var locMgrType = FindTypeAnyAssembly("SodaCraft.Localizations.LocalizationManager");
                if (i18nType != null && locMgrType != null)
                {
                    var keyField = i18nType.GetField("NotEnoughMoneyFormatKey", BindingFlags.Public | BindingFlags.Static);
                    var keyVal = keyField?.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(keyVal))
                    {
                        var toPlain = locMgrType.GetMethod("ToPlainText", BindingFlags.Public | BindingFlags.Static);
                        var txt = toPlain?.Invoke(null, new object[] { keyVal }) as string;
                        if (!string.IsNullOrEmpty(txt)) message = txt.Replace("{price}", amount.ToString());
                    }
                }

                if (string.IsNullOrEmpty(message))
                    message = $"Not enough money ({amount})";

                // NotificationText.Push(message)
                var notiType = FindTypeAnyAssembly("Duckov.UI.NotificationText") ??
                               Type.GetType("Duckov.UI.NotificationText, DuckovLuckyBox");
                var push = notiType?.GetMethod("Push", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                push?.Invoke(null, new object[] { message });
            }
            catch { /* ignore */ }
        }

        private static Type FindTypeAnyAssembly(string fullName)
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var t = asm.GetType(fullName, false);
                        if (t != null) return t;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        // ───────────────────── CFG: Load / Save ─────────────────────
        private void LoadConfig()
        {
            try
            {
                if (string.IsNullOrEmpty(_cfgPath))
                    _cfgPath = UnityEngine.Application.persistentDataPath + "/ItemReroll.cfg";

                // 파일 없으면 생성
                if (!global::System.IO.File.Exists(_cfgPath))
                {
                    var defaultText = CFG_HEADER +
                                      "RerollKey=F9\n" +
                                      "CostStep=1000\n" +
                                      "UseCost=1\n" +
                                      "DebugCurrency=0\n" +
                                      "# CurrencyHintType=\n" +
                                      "# CurrencyHintMember=\n" +
                                      "# CurrencyGameObject=\n" +
                                      "# CurrencyMethodGet=\n" +
                                      "# CurrencyMethodSet=\n" +
                                      "# CurrencyMethodAdd=\n";
                    EnsureDirExists(_cfgPath);
                    global::System.IO.File.WriteAllText(_cfgPath, defaultText);
                    _rerollKey = KeyCode.F9;
                    _rerollCostStep = 1000;
                    _useCost = true;
                    return;
                }

                string hintType = null, hintMember = null, goName = null, mGet = null, mSet = null, mAdd = null;
                int debugCurrency = 0;

                var lines = global::System.IO.File.ReadAllLines(_cfgPath);
                foreach (var raw in lines)
                {
                    var line = raw == null ? null : raw.Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line.StartsWith("#")) continue;
                    var idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    var key = line.Substring(0, idx).Trim();
                    var val = line.Substring(idx + 1).Trim();

                    if (string.Equals(key, "RerollKey", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Enum.TryParse<KeyCode>(val, true, out var parsed) &&
                            parsed != KeyCode.None && parsed != KeyCode.Escape)
                            _rerollKey = parsed;
                    }
                    else if (string.Equals(key, "CostStep", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(val, out var cs) && cs >= 0)
                            _rerollCostStep = cs;
                    }
                    else if (string.Equals(key, "UseCost", StringComparison.OrdinalIgnoreCase))
                    {
                        _useCost = (val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase));
                    }
                    else if (string.Equals(key, "DebugCurrency", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(val, out debugCurrency);
                    }
                    else if (string.Equals(key, "CurrencyHintType", StringComparison.OrdinalIgnoreCase))   hintType = string.IsNullOrWhiteSpace(val) ? null : val;
                    else if (string.Equals(key, "CurrencyHintMember", StringComparison.OrdinalIgnoreCase)) hintMember = string.IsNullOrWhiteSpace(val) ? null : val;
                    else if (string.Equals(key, "CurrencyGameObject", StringComparison.OrdinalIgnoreCase)) goName = string.IsNullOrWhiteSpace(val) ? null : val;
                    else if (string.Equals(key, "CurrencyMethodGet", StringComparison.OrdinalIgnoreCase))  mGet = string.IsNullOrWhiteSpace(val) ? null : val;
                    else if (string.Equals(key, "CurrencyMethodSet", StringComparison.OrdinalIgnoreCase))  mSet = string.IsNullOrWhiteSpace(val) ? null : val;
                    else if (string.Equals(key, "CurrencyMethodAdd", StringComparison.OrdinalIgnoreCase))  mAdd = string.IsNullOrWhiteSpace(val) ? null : val;
                }

                // 통화 브릿지 힌트 주입
                _currency.SetHints(hintType, hintMember, goName, mGet, mSet, mAdd, debugCurrency != 0);

                Debug.Log($"[ItemReroll] (CFG) 키:{_rerollKey} step:{_rerollCostStep} useCost:{_useCost}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ItemReroll] CFG 로드 실패: {ex.Message}");
            }
        }

        private void SaveConfig()
        {
            try
            {
                if (string.IsNullOrEmpty(_cfgPath))
                    _cfgPath = UnityEngine.Application.persistentDataPath + "/ItemReroll.cfg";

                EnsureDirExists(_cfgPath);
                var text = CFG_HEADER +
                           $"RerollKey={_rerollKey}\n" +
                           $"CostStep={_rerollCostStep}\n" +
                           $"UseCost={( _useCost ? 1 : 0)}\n" +
                           $"DebugCurrency={( _currency.DebugLog ? 1 : 0)}\n";
                global::System.IO.File.WriteAllText(_cfgPath, text);
                Debug.Log($"[ItemReroll] (CFG) 저장 완료: {_cfgPath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ItemReroll] CFG 저장 실패: {ex.Message}");
            }
        }

        private static void EnsureDirExists(string filePath)
        {
            try
            {
                var dir = global::System.IO.Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !global::System.IO.Directory.Exists(dir))
                    global::System.IO.Directory.CreateDirectory(dir);
            }
            catch { }
        }
        // ─────────────────────────────────────────────────────────────

        private void LoadRerollKey()
        {
            if (PlayerPrefs.HasKey(PREFS_REROLL_KEY))
            {
                var saved = PlayerPrefs.GetString(PREFS_REROLL_KEY);
                if (Enum.TryParse<KeyCode>(saved, true, out var key) &&
                    key != KeyCode.None && key != KeyCode.Escape)
                {
                    if (_rerollKey == KeyCode.F9) _rerollKey = key;
                }
            }
            Debug.Log($"[ItemReroll] 리롤 키: {_rerollKey}");
        }

        public void SetRerollKey(string keyName)
        {
            if (Enum.TryParse<KeyCode>(keyName, true, out var key) &&
                key != KeyCode.None && key != KeyCode.Escape)
            {
                _rerollKey = key;
                PlayerPrefs.SetString(PREFS_REROLL_KEY, key.ToString());
                PlayerPrefs.Save();
                SaveConfig();
                Debug.Log($"[ItemReroll] 리롤 키가 '{(_rerollKey)}' 로 저장되었습니다.");
            }
            else
            {
                Debug.LogError($"[ItemReroll] 잘못된 키 이름: {keyName}");
            }
        }
    }

    // ───────────────────────── 아이템 DB ─────────────────────────
    internal class ItemDatabase
    {
        private readonly List<int> _validItemIDs = new List<int>();
        private readonly Dictionary<int, int> _stackCounts = new Dictionary<int, int>();

        public int Count => _validItemIDs.Count;
        public int MinID => _validItemIDs.Count > 0 ? _validItemIDs.Min() : 0;
        public int MaxID => _validItemIDs.Count > 0 ? _validItemIDs.Max() : 0;

        public void LoadFromGame()
        {
            try
            {
                var collectionType = ReflectionHelper.FindType("ItemAssetsCollection");
                if (collectionType == null) return;

                var collection = Resources.LoadAll<ScriptableObject>("")
                    .FirstOrDefault(obj => collectionType.IsAssignableFrom(obj.GetType()));
                if (collection == null) return;

                var itemType = ReflectionHelper.FindType("ItemStatsSystem.Item");
                if (itemType == null) return;

                int vanillaCount = LoadVanillaItems(collectionType, collection, itemType);
                int moddedCount = LoadModdedItems(collectionType, itemType);

                Debug.Log($"[ItemReroll] 로딩 완료: 바닐라 {vanillaCount}개, 모드 {moddedCount}개, 총 {Count}개");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ItemReroll] 아이템 로딩 실패: {ex.Message}");
            }
        }

        public int GetRandomID()
        {
            if (_validItemIDs.Count == 0)
                throw new InvalidOperationException("[ItemReroll] 유효 아이템 풀이 비었습니다. (더미 제외 후)");
            return _validItemIDs[UnityEngine.Random.Range(0, _validItemIDs.Count)];
        }

        public int GetMaxStack(int itemID)
        {
            return _stackCounts.TryGetValue(itemID, out int maxStack) ? maxStack : 1;
        }

        private int LoadVanillaItems(Type collectionType, ScriptableObject collection, Type itemType)
        {
            int count = 0;
            var entriesField = collectionType.GetField("entries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (entriesField == null) return 0;

            var entries = entriesField.GetValue(collection) as System.Collections.IList;
            if (entries == null) return 0;

            foreach (var entry in entries)
            {
                if (TryExtractItemData(entry, itemType, out int typeID, out int maxStack))
                {
                    AddItem(typeID, maxStack);
                    count++;
                }
            }
            return count;
        }

        private int LoadModdedItems(Type collectionType, Type itemType)
        {
            int count = 0;
            var dynamicDicField = collectionType.GetField("dynamicDic", BindingFlags.Static | BindingFlags.NonPublic);
            if (dynamicDicField == null) return 0;

            var dynamicDic = dynamicDicField.GetValue(null) as System.Collections.IDictionary;
            if (dynamicDic == null) return 0;

            foreach (System.Collections.DictionaryEntry entry in dynamicDic)
            {
                if (TryExtractItemData(entry.Value, itemType, out int typeID, out int maxStack))
                {
                    AddItem(typeID, maxStack);
                    count++;
                }
            }
            return count;
        }

        private bool TryExtractItemData(object entry, Type itemType, out int typeID, out int maxStack)
        {
            typeID = 0;
            maxStack = 1;

            if (entry == null) return false;

            try
            {
                var typeIDField = entry.GetType().GetField("typeID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (typeIDField != null)
                    typeID = (int)typeIDField.GetValue(entry);

                if (typeID <= 0) return false;

                var prefabField = entry.GetType().GetField("prefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prefabField == null) return false;

                var prefabObj = prefabField.GetValue(entry);
                GameObject gameObject = null;

                if (prefabObj is Component component) gameObject = component.gameObject;
                else if (prefabObj is GameObject go) gameObject = go;

                if (gameObject == null) return false;

                var itemComponent = gameObject.GetComponent(itemType);
                if (itemComponent == null) return false;

                if (!IsValidItem(itemComponent, itemType)) return false;

                var maxStackProp = itemType.GetProperty("MaxStackCount");
                if (maxStackProp != null && maxStackProp.GetValue(itemComponent) is int stack)
                    maxStack = stack;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool IsValidItem(object itemComponent, Type itemType)
        {
            var iconProp = itemType.GetProperty("Icon");
            var nameProp = itemType.GetProperty("DisplayName");

            var icon = iconProp?.GetValue(itemComponent) as Sprite;
            var displayName = nameProp?.GetValue(itemComponent) as string;

            return icon != null && !string.IsNullOrEmpty(displayName);
        }

        private void AddItem(int typeID, int maxStack)
        {
            // 더미 즉시 차단
            if (!ItemReroll.DummyItemIds.IsAllowed(typeID))
                return;

            if (!_validItemIDs.Contains(typeID))
            {
                _validItemIDs.Add(typeID);
                _stackCounts[typeID] = maxStack;
            }
        }
    }

    // ───────────────────────── 필터 ─────────────────────────
    internal class ItemFilter
    {
        private static readonly string[] ContainerKeywords =
        {
            "LootBox_EnemyDie",
            "LootBox_Natural",
            "Container",
            "Chest",
            "Box",
            "Drawer"
        };

        public FilterResult FilterContainerItems(Item[] allItems)
        {
            var result = new FilterResult();
            foreach (Item item in allItems)
            {
                if (item == null || item.gameObject == null) { result.NullCount++; continue; }
                if (item.transform.parent == null) { result.GroundCount++; continue; }

                string parentName = item.transform.parent.name;
                if (string.IsNullOrEmpty(parentName)) { result.NullCount++; continue; }

                if (parentName.Contains("Character")) { result.PlayerCount++; continue; }
                if (parentName.StartsWith("Agent_Pickup") || parentName.Contains("Pickup")) { result.GroundCount++; continue; }
                if (parentName.Contains("Tomb")) { result.TombCount++; continue; }
                if (item.transform.parent.GetComponent<Item>() != null) { result.InventoryCount++; continue; }
                if (item.transform.parent.GetComponent<Inventory>() == null) { result.InventoryCount++; continue; }

                if (IsContainerItem(parentName)) result.ContainerItems.Add(item);
                else result.InventoryCount++;
            }
            return result;
        }

        private bool IsContainerItem(string parentName)
        {
            return ContainerKeywords.Any(keyword => parentName.Contains(keyword));
        }

        public class FilterResult
        {
            public List<Item> ContainerItems { get; } = new List<Item>();
            public int PlayerCount { get; set; }
            public int TombCount { get; set; }
            public int GroundCount { get; set; }
            public int InventoryCount { get; set; }
            public int NullCount { get; set; }
        }
    }

    // ───────────────────────── 리롤러 ─────────────────────────
    internal class ItemReroller
    {
        public bool RerollItem(Item originalItem, ItemDatabase database)
        {
            if (originalItem == null) return false;
            try
            {
                var inventory = GetInventory(originalItem);
                if (inventory == null) return false;

                int index = GetItemIndex(inventory, originalItem);
                if (index < 0) return false;

                var newItem = CreateRandomItem(database);
                if (newItem == null) return false;

                SetRandomStack(newItem, database);

                if (!RemoveItem(inventory, index)) return false;
                if (!AddItem(inventory, newItem, index)) return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private Inventory GetInventory(Item item)
        {
            var parent = item.transform.parent;
            if (parent == null) return null;
            return parent.GetComponent<Inventory>();
        }

        private int GetItemIndex(Inventory inventory, Item item)
        {
            var getIndexMethod = inventory.GetType().GetMethod("GetIndex");
            if (getIndexMethod == null) return -1;
            return (int)getIndexMethod.Invoke(inventory, new object[] { item });
        }

        private Item CreateRandomItem(ItemDatabase database)
        {
            int randomID = database.GetRandomID();
            var collectionType = ReflectionHelper.FindType("ItemAssetsCollection");
            if (collectionType == null) return null;
            var instantiateMethod = collectionType.GetMethod("InstantiateSync", BindingFlags.Public | BindingFlags.Static);
            if (instantiateMethod == null) return null;
            var newItemObj = instantiateMethod.Invoke(null, new object[] { randomID });
            return newItemObj as Item;
        }

        private void SetRandomStack(Item item, ItemDatabase database)
        {
            int maxStack = database.GetMaxStack(item.TypeID);
            if (maxStack <= 1) return;
            int randomStack = UnityEngine.Random.Range(1, maxStack + 1);
            var stackProp = item.GetType().GetProperty("Stack") ?? item.GetType().GetProperty("Quantity");
            if (stackProp != null && stackProp.CanWrite) stackProp.SetValue(item, randomStack);
        }

        private bool RemoveItem(Inventory inventory, int index)
        {
            var removeAtMethod = inventory.GetType().GetMethod("RemoveAt");
            if (removeAtMethod == null) return false;
            object[] removeParams = new object[] { index, null };
            bool removed = (bool)removeAtMethod.Invoke(inventory, removeParams);
            var removedItem = removeParams[1] as Item;
            if (removedItem != null) UnityEngine.Object.Destroy(removedItem.gameObject);
            return removed;
        }

        private bool AddItem(Inventory inventory, Item item, int index)
        {
            var addAtMethod = inventory.GetType().GetMethod("AddAt");
            if (addAtMethod == null) return false;
            return (bool)addAtMethod.Invoke(inventory, new object[] { item, index });
        }
    }

    // ─────────────────── 리플렉션 헬퍼 + 통화 브릿지 ───────────────────
    internal static class ReflectionHelper
    {
        public static Type FindType(string typeName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(assembly => GetTypesFromAssembly(assembly))
                .FirstOrDefault(type => type.Name == typeName || type.FullName == typeName);
        }

        public static object GetSingletonOrAnyInstance(Type t)
        {
            // Common singleton patterns
            var props = new[] { "Instance", "instance", "Inst", "inst", "Singleton", "singleton" };
            foreach (var p in props)
            {
                var pi = t.GetProperty(p, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi != null)
                {
                    var val = pi.GetValue(null);
                    if (val != null) return val;
                }
            }
            var fields = new[] { "Instance", "instance", "Inst", "inst", "Singleton", "singleton" };
            foreach (var f in fields)
            {
                var fi = t.GetField(f, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi != null)
                {
                    var val = fi.GetValue(null);
                    if (val != null) return val;
                }
            }
            var comp = UnityEngine.Object.FindObjectOfType(t) as Component;
            if (comp != null) return comp;
            return null;
        }

        private static Type[] GetTypesFromAssembly(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch { return Array.Empty<Type>(); }
        }
    }

    internal class CurrencyBridge
    {
        private object _target;
        private FieldInfo _field;
        private PropertyInfo _prop;
        private MethodInfo _mGet, _mSet, _mAdd;

        public bool DebugLog { get; private set; }

        private string _hintType;
        private string _hintMember;
        private string _goName;
        private string _hintMGet, _hintMSet, _hintMAdd;

        private static readonly string[] CandidateNames = {
            "Money","money","Credits","credits","Gold","gold",
            "Balance","balance","Currency","currency","Cash","cash"
        };

        public void SetHints(string typeName, string memberName, string goName, string mGet, string mSet, string mAdd, bool debug)
        {
            _hintType = typeName;
            _hintMember = memberName;
            _goName = goName;
            _hintMGet = mGet;
            _hintMSet = mSet;
            _hintMAdd = mAdd;
            DebugLog = debug;
        }

        public void WarmUp()
        {
            TryLocateTarget();
        }

        private void Log(string msg)
        {
            if (DebugLog) Debug.Log("[ItemReroll][CurrencyBridge] " + msg);
        }

        private bool TryLocateTarget()
        {
            // 1) 힌트 기반 탐색
            if (!string.IsNullOrEmpty(_hintType) || !string.IsNullOrEmpty(_hintMember) ||
                !string.IsNullOrEmpty(_goName) || !string.IsNullOrEmpty(_hintMGet) ||
                !string.IsNullOrEmpty(_hintMSet) || !string.IsNullOrEmpty(_hintMAdd))
            {
                if (TryLocateWithHints()) return true;
            }

            // 2) 휴리스틱
            try
            {
                var tagged = GameObject.FindWithTag("Player");
                if (tagged != null && TryScanObject(tagged)) return true;

                foreach (var t in GameObject.FindObjectsOfType<Transform>())
                {
                    var n = t.name;
                    if (string.IsNullOrEmpty(n)) continue;
                    if (n.Contains("Player") || n.Contains("Character"))
                    {
                        if (TryScanObject(t.gameObject)) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private bool TryLocateWithHints()
        {
            Log("TryLocateWithHints()");
            GameObject root = null;
            if (!string.IsNullOrEmpty(_goName))
            {
                foreach (var t in GameObject.FindObjectsOfType<Transform>())
                {
                    if (!string.IsNullOrEmpty(t.name) && t.name.IndexOf(_goName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        root = t.gameObject;
                        break;
                    }
                }
                Log("GO search: " + (root ? root.name : "not found"));
            }

            Type tComp = null;
            if (!string.IsNullOrEmpty(_hintType))
            {
                tComp = ReflectionHelper.FindType(_hintType);
                Log("Type hint: " + (tComp != null ? tComp.FullName : "not found"));
            }

            object instance = null;
            if (tComp != null)
            {
                if (root != null)
                {
                    var comp = root.GetComponentInChildren(tComp, true);
                    if (comp != null) instance = comp;
                }
                if (instance == null)
                {
                    instance = ReflectionHelper.GetSingletonOrAnyInstance(tComp);
                }
            }
            else if (root != null)
            {
                foreach (var c in root.GetComponentsInChildren<Component>(true))
                {
                    if (TryBindOnComponent(c, _hintMember)) { instance = c; break; }
                }
            }

            if (instance != null)
            {
                var instType = instance.GetType();
                if (!string.IsNullOrEmpty(_hintMGet))
                    _mGet = instType.GetMethod(_hintMGet, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!string.IsNullOrEmpty(_hintMSet))
                    _mSet = instType.GetMethod(_hintMSet, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null)
                         ?? instType.GetMethod(_hintMSet, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(float) }, null);
                if (!string.IsNullOrEmpty(_hintMAdd))
                    _mAdd = instType.GetMethod(_hintMAdd, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null)
                         ?? instType.GetMethod(_hintMAdd, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(float) }, null);

                if ((_mGet != null && (_mSet != null || _mAdd != null)) ||
                    TryBindOnComponent(instance as Component, _hintMember))
                {
                    _target = instance;
                    Log("Bind success via hints on: " + instType.FullName);
                    return true;
                }
            }

            // 타입만 있고 인스턴스를 못 찾은 경우
            if (tComp != null && instance == null)
            {
                instance = ReflectionHelper.GetSingletonOrAnyInstance(tComp);
                if (instance != null)
                {
                    if ((_mGet != null && (_mSet != null || _mAdd != null)) ||
                        TryBindOnAnyMember(instance, _hintMember))
                    {
                        _target = instance;
                        Log("Bind success via type singleton: " + instance.GetType().FullName);
                        return true;
                    }
                }
            }

            foreach (var c in GameObject.FindObjectsOfType<Component>())
            {
                if (TryBindOnComponent(c, _hintMember))
                {
                    _target = c;
                    Log("Bind success via scene scan: " + c.GetType().FullName);
                    return true;
                }
            }

            Log("Bind failed via hints.");
            return false;
        }

        private bool TryBindOnComponent(Component comp, string preferMember)
        {
            if (comp == null) return false;
            var type = comp.GetType();

            if (!string.IsNullOrEmpty(preferMember))
            {
                var p = type.GetProperty(preferMember, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && (p.PropertyType == typeof(int) || p.PropertyType == typeof(float)) && p.CanRead)
                { _prop = p; _field = null; return true; }

                var f = type.GetField(preferMember, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null && (f.FieldType == typeof(int) || f.FieldType == typeof(float)))
                { _field = f; _prop = null; return true; }
            }

            foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!p.CanRead) continue;
                if (!(p.PropertyType == typeof(int) || p.PropertyType == typeof(float))) continue;
                if (!CandidateNames.Any(cn => string.Equals(cn, p.Name, StringComparison.OrdinalIgnoreCase))) continue;

                if (p.CanWrite) { _prop = p; _field = null; return true; }
            }
            foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!(f.FieldType == typeof(int) || f.FieldType == typeof(float))) continue;
                if (!CandidateNames.Any(cn => string.Equals(cn, f.Name, StringComparison.OrdinalIgnoreCase))) continue;

                _field = f; _prop = null; return true;
            }
            return false;
        }

        private bool TryBindOnAnyMember(object instance, string preferMember)
        {
            var comp = instance as Component;
            if (comp != null) return TryBindOnComponent(comp, preferMember);

            var type = instance.GetType();
            if (!string.IsNullOrEmpty(preferMember))
            {
                var p = type.GetProperty(preferMember, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && (p.PropertyType == typeof(int) || p.PropertyType == typeof(float)) && p.CanRead)
                { _target = instance; _prop = p; _field = null; return true; }

                var f = type.GetField(preferMember, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null && (f.FieldType == typeof(int) || f.FieldType == typeof(float)))
                { _target = instance; _field = f; _prop = null; return true; }
            }
            return false;
        }

        private bool TryScanObject(GameObject go)
        {
            foreach (var comp in go.GetComponentsInChildren<Component>(true))
            {
                if (TryBindOnComponent(comp, _hintMember)) { _target = comp; return true; }
            }
            return false;
        }

        public bool TryGetMoney(out int money)
        {
            money = 0;
            if (_target == null && !TryLocateTarget()) return false;

            try
            {
                if (_mGet != null)
                {
                    var val = _mGet.Invoke(_target, null);
                    if (val is int i1) { money = i1; return true; }
                    if (val is float f1) { money = Mathf.RoundToInt(f1); return true; }
                }

                object v = _prop != null ? _prop.GetValue(_target) : _field.GetValue(_target);
                if (v is int i) { money = i; return true; }
                if (v is float f) { money = Mathf.RoundToInt(f); return true; }
            }
            catch (Exception ex)
            {
                Log("TryGetMoney ex: " + ex.Message);
            }
            return false;
        }

        public bool TrySetMoney(int money)
        {
            if (_target == null && !TryLocateTarget()) return false;

            try
            {
                if (_mSet != null)
                {
                    var ps = _mSet.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(int))
                    { _mSet.Invoke(_target, new object[] { money }); return true; }
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(float))
                    { _mSet.Invoke(_target, new object[] { (float)money }); return true; }
                }

                if (_prop != null && _prop.CanWrite)
                {
                    if (_prop.PropertyType == typeof(int)) { _prop.SetValue(_target, money); return true; }
                    if (_prop.PropertyType == typeof(float)) { _prop.SetValue(_target, (float)money); return true; }
                }
                else if (_field != null && !_field.IsInitOnly)
                {
                    if (_field.FieldType == typeof(int)) { _field.SetValue(_target, money); return true; }
                    if (_field.FieldType == typeof(float)) { _field.SetValue(_target, (float)money); return true; }
                }
            }
            catch (Exception ex)
            {
                Log("TrySetMoney ex: " + ex.Message);
            }
            return false;
        }

        public bool TrySpendMoney(int amount)
        {
            if (_target == null && !TryLocateTarget()) return false;

            try
            {
                if (_mAdd != null)
                {
                    var ps = _mAdd.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(int))
                    { _mAdd.Invoke(_target, new object[] { -amount }); return true; }
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(float))
                    { _mAdd.Invoke(_target, new object[] { (float)(-amount) }); return true; }
                }
            }
            catch (Exception ex)
            {
                Log("TrySpendMoney ex: " + ex.Message);
            }
            return false;
        }
    }
}