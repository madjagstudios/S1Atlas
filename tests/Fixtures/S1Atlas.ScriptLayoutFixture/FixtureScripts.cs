using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fixture.Game
{
    public class Shop : MonoBehaviour
    {
        [SerializeField] private int Price;
        public float Wage;
        public string Label;
        public List<int> Stock;
        [NonSerialized] public int Ignored;
        private int NotSerialized;
    }

    public class Catalog : ScriptableObject
    {
        [SerializeField] private int MaxQuantity;
        public Color Tint;
    }

    // Mirrors a v1 (attribute-stripped) reconstruction: the payload still carries Price, but the
    // class no longer says it is serialized, so a template built from it misaligns.
    public class UnattributedShop : MonoBehaviour
    {
        private int Price;
        public float Wage;
    }

    public class Inventory : MonoBehaviour
    {
        public List<int> Stock;
    }

    public class Blob : MonoBehaviour
    {
        public byte[] Small;
        public byte[] Large;
    }
}
