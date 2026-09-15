using UnityEngine;

namespace HappyHarvest
{
    /// <summary>
    /// Marks a scene object as a usable NPC drinking point.
    /// Put this component on the exact transform where the NPC should stand.
    /// </summary>
    public sealed class NpcDrinkSource : MonoBehaviour, INpcDrinkSource
    {
        [SerializeField] bool canDrink = true;

        public bool CanDrink => isActiveAndEnabled && canDrink;

        public bool TryDrink(PlayerController npc)
        {
            return npc != null && CanDrink;
        }
    }
}
