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
        private const string LOG_PREFIX = "[ItemReroll]";
        private const KeyCode REROLL_KEY = KeyCode.F9;
        
        private readonly ItemDatabase _itemDatabase = new ItemDatabase();
        private readonly ItemFilter _itemFilter = new ItemFilter();
        private readonly ItemReroller _itemReroller = new ItemReroller();
        private bool _isRerolling;

        void Awake()
        {
            LogSection("모드 초기화 시작");
            Debug.Log($"{LOG_PREFIX} 리롤 대상: 컨테이너 아이템 (적 드롭, 자연 파밍 컨테이너)");
            
            _itemDatabase.LoadFromGame();
            
            Debug.Log($"{LOG_PREFIX} 유효한 아이템 ID: {_itemDatabase.Count}개");
            if (_itemDatabase.Count > 0)
            {
                Debug.Log($"{LOG_PREFIX} ID 범위: {_itemDatabase.MinID} ~ {_itemDatabase.MaxID}");
            }
            
            Debug.Log($"{LOG_PREFIX} 모드 로드 완료!");
            LogSection("");
        }
        
        void Update()
        {
            if (Input.GetKeyDown(REROLL_KEY))
            {
                if (_isRerolling)
                {
                    Debug.LogWarning($"{LOG_PREFIX} 이미 리롤이 진행 중입니다. 대기 중...");
                    return;
                }
                
                LogSection("F9 키 입력 감지! 리롤 시작...");
                StartCoroutine(PerformReroll());
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
            
            Debug.Log($"{LOG_PREFIX} [3단계] {containerItems.Count}개 컨테이너 아이템 리롤 시작...");
            
            int successCount = 0;
            int failCount = 0;
            
            for (int i = 0; i < containerItems.Count; i++)
            {
                Debug.Log($"{LOG_PREFIX} [{i + 1}/{containerItems.Count}] 아이템 처리 중...");
                
                bool success = _itemReroller.RerollItem(containerItems[i], _itemDatabase);
                
                if (success)
                    successCount++;
                else
                    failCount++;
                
                yield return null;
            }
            
            ShowResults(successCount, failCount);
            
            _isRerolling = false;
            Debug.Log($"{LOG_PREFIX} 리롤 프로세스 완료. 다음 리롤 대기 중...");
        }

        private List<Item> FindContainerItems()
        {
            Debug.Log($"{LOG_PREFIX} [1단계] 월드의 모든 아이템 탐색 중...");
            
            Item[] allItems = UnityEngine.Object.FindObjectsOfType<Item>();
            Debug.Log($"{LOG_PREFIX} 발견된 총 아이템 수: {allItems.Length}개");
            
            Debug.Log($"{LOG_PREFIX} [2단계] 아이템 필터링 중... (컨테이너 아이템만)");
            
            var filterResult = _itemFilter.FilterContainerItems(allItems);
            
            Debug.Log($"{LOG_PREFIX} 필터링 결과:");
            Debug.Log($"{LOG_PREFIX}   - 리롤 대상: {filterResult.ContainerItems.Count}개");
            Debug.Log($"{LOG_PREFIX}   - 플레이어: {filterResult.PlayerCount}개");
            Debug.Log($"{LOG_PREFIX}   - 플레이어 시체: {filterResult.TombCount}개");
            Debug.Log($"{LOG_PREFIX}   - 바닥: {filterResult.GroundCount}개");
            Debug.Log($"{LOG_PREFIX}   - 인벤토리: {filterResult.InventoryCount}개");
            Debug.Log($"{LOG_PREFIX}   - null/무효: {filterResult.NullCount}개");
            
            return filterResult.ContainerItems;
        }

        private void ShowResults(int successCount, int failCount)
        {
            Debug.Log($"{LOG_PREFIX} [4단계] 리롤 완료!");
            Debug.Log($"{LOG_PREFIX}   - 성공: {successCount}개");
            Debug.Log($"{LOG_PREFIX}   - 실패: {failCount}개");
            
            string message = successCount > 0 
                ? $"{successCount}개 컨테이너 아이템 리롤!" 
                : "리롤할 컨테이너 아이템이 없습니다.";
            
            LogSection(message);
            Debug.Log($"[알림] {message}");
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
    }

    internal class ItemDatabase
    {
        private const string LOG_PREFIX = "[ItemReroll]";
        
        private readonly List<int> _validItemIDs = new List<int>();
        private readonly Dictionary<int, int> _stackCounts = new Dictionary<int, int>();

        public int Count => _validItemIDs.Count;
        public int MinID => _validItemIDs.Count > 0 ? _validItemIDs.Min() : 0;
        public int MaxID => _validItemIDs.Count > 0 ? _validItemIDs.Max() : 0;

        public void LoadFromGame()
        {
            Debug.Log($"{LOG_PREFIX} 게임에서 아이템 ID 동적 로딩 중...");
            
            try
            {
                var collectionType = ReflectionHelper.FindType("ItemAssetsCollection");
                if (collectionType == null)
                {
                    Debug.LogError($"{LOG_PREFIX} ItemAssetsCollection 타입을 찾을 수 없습니다");
                    return;
                }
                
                var collection = Resources.LoadAll<ScriptableObject>("")
                    .FirstOrDefault(obj => collectionType.IsAssignableFrom(obj.GetType()));
                
                if (collection == null)
                {
                    Debug.LogError($"{LOG_PREFIX} ItemAssetsCollection 인스턴스를 찾을 수 없습니다");
                    return;
                }
                
                var itemType = ReflectionHelper.FindType("ItemStatsSystem.Item");
                if (itemType == null)
                {
                    Debug.LogError($"{LOG_PREFIX} Item 타입을 찾을 수 없습니다");
                    return;
                }
                
                int vanillaCount = LoadVanillaItems(collectionType, collection, itemType);
                int moddedCount = LoadModdedItems(collectionType, itemType);
                
                Debug.Log($"{LOG_PREFIX} 로딩 완료: 바닐라 {vanillaCount}개, 모드 {moddedCount}개, 총 {Count}개");
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LOG_PREFIX} 아이템 로딩 실패: {ex.Message}");
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
            if (entriesField == null)
            {
                Debug.LogWarning($"{LOG_PREFIX} entries 필드를 찾을 수 없습니다");
                return 0;
            }
            
            if (!(entriesField.GetValue(collection) is IList entries))
            {
                Debug.LogWarning($"{LOG_PREFIX} entries가 null입니다");
                return 0;
            }
            
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
            if (dynamicDicField == null)
            {
                Debug.Log($"{LOG_PREFIX} dynamicDic 필드 없음 (모드 아이템 없음)");
                return 0;
            }
            
            if (!(dynamicDicField.GetValue(null) is IDictionary dynamicDic))
            {
                Debug.Log($"{LOG_PREFIX} dynamicDic이 null (모드 아이템 없음)");
                return 0;
            }
            
            foreach (DictionaryEntry entry in dynamicDic)
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
                {
                    typeID = (int)typeIDField.GetValue(entry);
                }
                
                if (typeID <= 0) return false;
                  // 더미 즉시 차단
            if (!ItemReroll.DummyItemIds.IsAllowed(typeID)){
                 Debug.LogWarning($"{LOG_PREFIX} 더미아이템 제외: {typeID}");
             return;
            }
               
                var prefabField = entry.GetType().GetField("prefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prefabField == null) return false;
                
                var prefabObj = prefabField.GetValue(entry);
                GameObject gameObject = null;
                
                if (prefabObj is Component component)
                    gameObject = component.gameObject;
                else if (prefabObj is GameObject go)
                    gameObject = go;
                
                if (gameObject == null) return false;
                
                var itemComponent = gameObject.GetComponent(itemType);
                if (itemComponent == null) return false;
                
                if (!IsValidItem(itemComponent, itemType)) return false;
                
                var maxStackProp = itemType.GetProperty("MaxStackCount");
                if (maxStackProp != null && maxStackProp.GetValue(itemComponent) is int stack)
                {
                    maxStack = stack;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LOG_PREFIX} 항목 처리 실패: {ex.Message}");
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
          

            if (!_validItemIDs.Contains(typeID))
            {
                _validItemIDs.Add(typeID);
                _stackCounts[typeID] = maxStack;
            }
        }
    }

    internal class ItemFilter
    {
        private const string LOG_PREFIX = "[ItemReroll]";
        
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
            int itemIndex = 0;
            
            foreach (Item item in allItems)
            {
                itemIndex++;
                
                if (!IsValidItem(item))
                {
                    result.NullCount++;
                    continue;
                }
                
                string itemName = item.gameObject.name;
                int itemTypeID = item.TypeID;
                
                if (!HasParent(item))
                {
                    result.GroundCount++;
                    continue;
                }
                
                string parentName = item.transform.parent.name;
                
                if (IsPlayerItem(parentName))
                {
                    result.PlayerCount++;
                    Debug.Log($"{LOG_PREFIX}   [{itemIndex}] [플레이어 제외] '{itemName}' (ID:{itemTypeID})");
                    continue;
                }
                
                if (IsPickupItem(parentName))
                {
                    result.GroundCount++;
                    continue;
                }
                
                if (IsTombItem(parentName))
                {
                    result.TombCount++;
                    Debug.Log($"{LOG_PREFIX}   [{itemIndex}] [플레이어 시체 제외] '{itemName}' (ID:{itemTypeID}) - {parentName}");
                    continue;
                }
                
                if (IsNestedItem(item))
                {
                    result.InventoryCount++;
                    Debug.Log($"{LOG_PREFIX}   [{itemIndex}] [아이템 내부 제외] '{itemName}' (ID:{itemTypeID}) - Parent Item: {parentName}");
                    continue;
                }
                
                if (!HasInventory(item))
                {
                    result.InventoryCount++;
                    continue;
                }
                
                if (IsContainerItem(parentName))
                {
                    result.ContainerItems.Add(item);
                    Debug.Log($"{LOG_PREFIX}   [{itemIndex}] [리롤 대상] '{itemName}' (ID:{itemTypeID}) - 컨테이너: {parentName}");
                }
                else
                {
                    result.InventoryCount++;
                }
            }
            
            return result;
        }

        private bool IsValidItem(Item item) => item != null && item.gameObject != null;
        private bool HasParent(Item item) => item.transform.parent != null;
        private bool IsPlayerItem(string parentName) => parentName.Contains("Character");
        private bool IsPickupItem(string parentName) => parentName.StartsWith("Agent_Pickup") || parentName.Contains("Pickup");
        private bool IsTombItem(string parentName) => parentName.Contains("Tomb");
        private bool IsNestedItem(Item item) => item.transform.parent.GetComponent<Item>() != null;
        private bool HasInventory(Item item) => item.transform.parent.GetComponent<Inventory>() != null;
        
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

    internal class ItemReroller
    {
        private const string LOG_PREFIX = "[ItemReroll]";

        public bool RerollItem(Item originalItem, ItemDatabase database)
        {
            if (originalItem == null)
            {
                Debug.LogWarning($"{LOG_PREFIX} [리롤 실패] 원본 아이템이 null");
                return false;
            }
            
            try
            {
                var inventory = GetInventory(originalItem);
                if (inventory == null) return false;
                
                int index = GetItemIndex(inventory, originalItem);
                if (index < 0) return false;
                
                var newItem = CreateRandomItem(database);
                if (newItem == null) return false;
                
                SetRandomStack(newItem, database);
                
                string oldName = originalItem.DisplayName;
                int oldID = originalItem.TypeID;
                
                if (!RemoveItem(inventory, index)) return false;
                if (!AddItem(inventory, newItem, index)) return false;
                
                Debug.Log($"{LOG_PREFIX} [리롤 성공] {oldName}(ID:{oldID}) -> {newItem.DisplayName}(ID:{newItem.TypeID})");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LOG_PREFIX} [리롤 실패] {originalItem.name} - {ex.Message}");
                Debug.LogError($"{LOG_PREFIX} 스택 트레이스: {ex.StackTrace}");
                return false;
            }
        }

        private Inventory GetInventory(Item item)
        {
            if (item.transform.parent == null)
            {
                Debug.LogWarning($"{LOG_PREFIX} [리롤 실패] {item.name} - Parent 없음");
                return null;
            }
            
            string parentName = item.transform.parent.name;
            Debug.Log($"{LOG_PREFIX}   - Parent: {parentName}");
            
            var inventory = item.transform.parent.GetComponent<Inventory>();
            if (inventory == null)
            {
                Debug.LogWarning($"{LOG_PREFIX} [리롤 실패] {item.name} - Parent에 Inventory 컴포넌트 없음 (Parent: {parentName})");
                return null;
            }
            
            Debug.Log($"{LOG_PREFIX}   - Inventory 발견: {inventory.name}");
            return inventory;
        }

        private int GetItemIndex(Inventory inventory, Item item)
        {
            var getIndexMethod = inventory.GetType().GetMethod("GetIndex");
            if (getIndexMethod == null)
            {
                Debug.LogError($"{LOG_PREFIX} [리롤 실패] {item.name} - GetIndex 메서드를 찾을 수 없음");
                return -1;
            }
            
            Debug.Log($"{LOG_PREFIX}   - GetIndex 메서드 호출 중...");
            int index = (int)getIndexMethod.Invoke(inventory, new object[] { item });
            Debug.Log($"{LOG_PREFIX}   - 인덱스: {index}");
            
            if (index < 0)
            {
                Debug.LogWarning($"{LOG_PREFIX} [리롤 실패] {item.name} - 인덱스를 찾을 수 없음");
            }
            
            return index;
        }

        private Item CreateRandomItem(ItemDatabase database)
        {
            int randomID = database.GetRandomID();
            Debug.Log($"{LOG_PREFIX}   - 랜덤 ID 선택: {randomID}");
            
            var collectionType = ReflectionHelper.FindType("ItemAssetsCollection");
            if (collectionType == null)
            {
                Debug.LogError($"{LOG_PREFIX} [리롤 실패] ItemAssetsCollection 타입을 찾을 수 없음");
                return null;
            }
            
            var instantiateMethod = collectionType.GetMethod("InstantiateSync", BindingFlags.Public | BindingFlags.Static);
            if (instantiateMethod == null)
            {
                Debug.LogError($"{LOG_PREFIX} [리롤 실패] InstantiateSync 메서드를 찾을 수 없음");
                return null;
            }
            
            Debug.Log($"{LOG_PREFIX}   - InstantiateSync 호출 중...");
            var newItemObj = instantiateMethod.Invoke(null, new object[] { randomID });
            var newItem = newItemObj as Item;
            
            Debug.Log($"{LOG_PREFIX}   - 새 아이템 생성: {(newItem != null ? newItem.name : "null")}");
            
            if (newItem == null)
            {
                Debug.LogError($"{LOG_PREFIX} [리롤 실패] ID {randomID} 생성 실패");
            }
            
            return newItem;
        }

        private void SetRandomStack(Item item, ItemDatabase database)
        {
            int maxStack = database.GetMaxStack(item.TypeID);
            if (maxStack <= 1) return;
            
            int randomStack = UnityEngine.Random.Range(1, maxStack + 1);
            
            var stackProp = item.GetType().GetProperty("Stack") ?? item.GetType().GetProperty("Quantity");
            if (stackProp != null && stackProp.CanWrite)
            {
                stackProp.SetValue(item, randomStack);
                Debug.Log($"{LOG_PREFIX}   - Stack 설정: {randomStack}/{maxStack}");
            }
        }

        private bool RemoveItem(Inventory inventory, int index)
        {
            Debug.Log($"{LOG_PREFIX}   - 원본 아이템 제거 중...");
            
            var removeAtMethod = inventory.GetType().GetMethod("RemoveAt");
            if (removeAtMethod == null)
            {
                Debug.LogError($"{LOG_PREFIX} [리롤 실패] RemoveAt 메서드를 찾을 수 없음");
                return false;
            }
            
            object[] removeParams = new object[] { index, null };
            bool removed = (bool)removeAtMethod.Invoke(inventory, removeParams);
            
            if (!removed)
            {
                Debug.LogError($"{LOG_PREFIX} [리롤 실패] 아이템 제거 실패");
                return false;
            }
            
            Debug.Log($"{LOG_PREFIX}   - 원본 아이템 제거 완료");
            
            var removedItem = removeParams[1] as Item;
            if (removedItem != null)
            {
                UnityEngine.Object.Destroy(removedItem.gameObject);
            }
            
            return true;
        }

        private bool AddItem(Inventory inventory, Item item, int index)
        {
            Debug.Log($"{LOG_PREFIX}   - AddAt 메서드 찾는 중...");
            
            var addAtMethod = inventory.GetType().GetMethod("AddAt");
            if (addAtMethod == null)
            {
                Debug.LogError($"{LOG_PREFIX} [리롤 실패] AddAt 메서드를 찾을 수 없음");
                return false;
            }
            
            Debug.Log($"{LOG_PREFIX}   - AddAt 호출: 인덱스={index}, 새 아이템={item.name}");
            bool added = (bool)addAtMethod.Invoke(inventory, new object[] { item, index });
            
            if (!added)
            {
                Debug.LogError($"{LOG_PREFIX} [리롤 실패] 새 아이템 추가 실패");
                return false;
            }
            
            Debug.Log($"{LOG_PREFIX}   - AddAt 완료");
            return true;
        }
    }

    internal static class ReflectionHelper
    {
        public static Type FindType(string typeName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(assembly => GetTypesFromAssembly(assembly))
                .FirstOrDefault(type => type.Name == typeName || type.FullName == typeName);
        }

        private static Type[] GetTypesFromAssembly(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }
    }
}
