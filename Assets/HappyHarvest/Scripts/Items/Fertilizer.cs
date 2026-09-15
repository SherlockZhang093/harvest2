using UnityEngine;

namespace HappyHarvest
{
    [CreateAssetMenu(fileName = "Fertilizer", menuName = "2D Farming/Items/Fertilizer")]
    public class Fertilizer : Item
    {
        public override bool CanUse(Vector3Int target)
        {
            var terrain = GameManager.Instance.Terrain;
            return terrain != null && terrain.State.GetPlot(target)?.NeedsFertilizer == true;
        }

        public override bool Use(Vector3Int target)
        {
            return GameManager.Instance.Terrain.TryFertilizeAt(target);
        }
    }
}
