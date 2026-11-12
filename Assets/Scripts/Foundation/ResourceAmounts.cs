using System;

namespace VaticanDefendor
{
    public enum ResourceKind : byte
    {
        None,
        Wood,
        Stone,
        Iron
    }

    [Serializable]
    public struct ResourceAmounts
    {
        public long Wood;
        public long Stone;
        public long Iron;

        public ResourceAmounts(long Wood, long Stone = 0, long Iron = 0)
        {
            this.Wood = Wood;
            this.Stone = Stone;
            this.Iron = Iron;
        }

        public bool IsValid => Wood >= 0 && Stone >= 0 && Iron >= 0;
        public bool IsEmpty => Wood == 0 && Stone == 0 && Iron == 0;

        public bool Covers(ResourceAmounts Cost)
        {
            return Cost.IsValid && Wood >= Cost.Wood && Stone >= Cost.Stone && Iron >= Cost.Iron;
        }

        public long Get(ResourceKind Kind)
        {
            switch (Kind)
            {
                case ResourceKind.Wood: return Wood;
                case ResourceKind.Stone: return Stone;
                case ResourceKind.Iron: return Iron;
                default: return 0;
            }
        }

        public static ResourceAmounts For(ResourceKind Kind, long Amount)
        {
            switch (Kind)
            {
                case ResourceKind.Wood: return new ResourceAmounts(Amount);
                case ResourceKind.Stone: return new ResourceAmounts(0, Amount);
                case ResourceKind.Iron: return new ResourceAmounts(0, 0, Amount);
                default: return default;
            }
        }

        public static ResourceAmounts operator +(ResourceAmounts Left, ResourceAmounts Right)
        {
            return new ResourceAmounts(checked(Left.Wood + Right.Wood),
                checked(Left.Stone + Right.Stone), checked(Left.Iron + Right.Iron));
        }

        public static ResourceAmounts operator -(ResourceAmounts Left, ResourceAmounts Right)
        {
            return new ResourceAmounts(checked(Left.Wood - Right.Wood),
                checked(Left.Stone - Right.Stone), checked(Left.Iron - Right.Iron));
        }

        public static ResourceAmounts operator *(ResourceAmounts Value, int Count)
        {
            return new ResourceAmounts(checked(Value.Wood * Count),
                checked(Value.Stone * Count), checked(Value.Iron * Count));
        }

        public override string ToString()
        {
            return $"{Wood} / {Stone} / {Iron}";
        }
    }
}
