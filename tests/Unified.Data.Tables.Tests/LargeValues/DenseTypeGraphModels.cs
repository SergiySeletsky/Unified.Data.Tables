namespace Unified.Data.Tables.Tests.LargeValues;

// A dense type graph: 14 types that each have a property of every other one (and a leaf). Generic in
// the leaf so the same graph exists once with nothing opted in and once with a [TableValueSplit] member.

/// <summary>A leaf that opts into nothing.</summary>
public class DensePlainLeaf
{
    public string? Name { get; set; }
}

/// <summary>A leaf with a large-value member, reachable from every type of the graph.</summary>
public class DenseSplitLeaf
{
    [TableValueSplit(SplitEncoding.Text)]
    public string? Body { get; set; }
}

public class DenseDoc<TLeaf> : Entity
{
    public Dense01<TLeaf>? Root { get; set; }
}

/// <summary>
/// A generic type whose property is a deeper instantiation of itself: every level is a new type, so
/// the chain of declared types never ends (a value's chain does).
/// </summary>
public class EndlessChain<T>
{
    public string? Name { get; set; }

    public EndlessChain<EndlessChain<T>>? Next { get; set; }
}

public class EndlessChainDoc : Entity
{
    public EndlessChain<int>? Chain { get; set; }
}

/// <summary>The same endless chain with a large-value member at every level.</summary>
public class EndlessSplitChain<T>
{
    [TableValueSplit(SplitEncoding.Text)]
    public string? Body { get; set; }

    public EndlessSplitChain<EndlessSplitChain<T>>? Next { get; set; }
}

public class EndlessSplitChainDoc : Entity
{
    public EndlessSplitChain<int>? Chain { get; set; }
}


public class Dense01<TLeaf>
{
    public string? Name { get; set; }

    public TLeaf? Leaf { get; set; }

    public Dense02<TLeaf>? P02 { get; set; }

    public Dense03<TLeaf>? P03 { get; set; }

    public Dense04<TLeaf>? P04 { get; set; }

    public Dense05<TLeaf>? P05 { get; set; }

    public Dense06<TLeaf>? P06 { get; set; }

    public Dense07<TLeaf>? P07 { get; set; }

    public Dense08<TLeaf>? P08 { get; set; }

    public Dense09<TLeaf>? P09 { get; set; }

    public Dense10<TLeaf>? P10 { get; set; }

    public Dense11<TLeaf>? P11 { get; set; }

    public Dense12<TLeaf>? P12 { get; set; }

    public Dense13<TLeaf>? P13 { get; set; }

    public Dense14<TLeaf>? P14 { get; set; }
}

public class Dense02<TLeaf>
{
    public string? Name { get; set; }

    public TLeaf? Leaf { get; set; }

    public Dense01<TLeaf>? P01 { get; set; }

    public Dense03<TLeaf>? P03 { get; set; }

    public Dense04<TLeaf>? P04 { get; set; }

    public Dense05<TLeaf>? P05 { get; set; }

    public Dense06<TLeaf>? P06 { get; set; }

    public Dense07<TLeaf>? P07 { get; set; }

    public Dense08<TLeaf>? P08 { get; set; }

    public Dense09<TLeaf>? P09 { get; set; }

    public Dense10<TLeaf>? P10 { get; set; }

    public Dense11<TLeaf>? P11 { get; set; }

    public Dense12<TLeaf>? P12 { get; set; }

    public Dense13<TLeaf>? P13 { get; set; }

    public Dense14<TLeaf>? P14 { get; set; }
}

public class Dense03<TLeaf>
{
    public string? Name { get; set; }

    public TLeaf? Leaf { get; set; }

    public Dense01<TLeaf>? P01 { get; set; }

    public Dense02<TLeaf>? P02 { get; set; }

    public Dense04<TLeaf>? P04 { get; set; }

    public Dense05<TLeaf>? P05 { get; set; }

    public Dense06<TLeaf>? P06 { get; set; }

    public Dense07<TLeaf>? P07 { get; set; }

    public Dense08<TLeaf>? P08 { get; set; }

    public Dense09<TLeaf>? P09 { get; set; }

    public Dense10<TLeaf>? P10 { get; set; }

    public Dense11<TLeaf>? P11 { get; set; }

    public Dense12<TLeaf>? P12 { get; set; }

    public Dense13<TLeaf>? P13 { get; set; }

    public Dense14<TLeaf>? P14 { get; set; }
}

public class Dense04<TLeaf>
{
    public string? Name { get; set; }

    public TLeaf? Leaf { get; set; }

    public Dense01<TLeaf>? P01 { get; set; }

    public Dense02<TLeaf>? P02 { get; set; }

    public Dense03<TLeaf>? P03 { get; set; }

    public Dense05<TLeaf>? P05 { get; set; }

    public Dense06<TLeaf>? P06 { get; set; }

    public Dense07<TLeaf>? P07 { get; set; }

    public Dense08<TLeaf>? P08 { get; set; }

    public Dense09<TLeaf>? P09 { get; set; }

    public Dense10<TLeaf>? P10 { get; set; }

    public Dense11<TLeaf>? P11 { get; set; }

    public Dense12<TLeaf>? P12 { get; set; }

    public Dense13<TLeaf>? P13 { get; set; }

    public Dense14<TLeaf>? P14 { get; set; }
}

public class Dense05<TLeaf>
{
    public string? Name { get; set; }

    public TLeaf? Leaf { get; set; }

    public Dense01<TLeaf>? P01 { get; set; }

    public Dense02<TLeaf>? P02 { get; set; }

    public Dense03<TLeaf>? P03 { get; set; }

    public Dense04<TLeaf>? P04 { get; set; }

    public Dense06<TLeaf>? P06 { get; set; }

    public Dense07<TLeaf>? P07 { get; set; }

    public Dense08<TLeaf>? P08 { get; set; }

    public Dense09<TLeaf>? P09 { get; set; }

    public Dense10<TLeaf>? P10 { get; set; }

    public Dense11<TLeaf>? P11 { get; set; }

    public Dense12<TLeaf>? P12 { get; set; }

    public Dense13<TLeaf>? P13 { get; set; }

    public Dense14<TLeaf>? P14 { get; set; }
}

public class Dense06<TLeaf>
{
    public string? Name { get; set; }

    public TLeaf? Leaf { get; set; }

    public Dense01<TLeaf>? P01 { get; set; }

    public Dense02<TLeaf>? P02 { get; set; }

    public Dense03<TLeaf>? P03 { get; set; }

    public Dense04<TLeaf>? P04 { get; set; }

    public Dense05<TLeaf>? P05 { get; set; }

    public Dense07<TLeaf>? P07 { get; set; }

    public Dense08<TLeaf>? P08 { get; set; }

    public Dense09<TLeaf>? P09 { get; set; }

    public Dense10<TLeaf>? P10 { get; set; }

    public Dense11<TLeaf>? P11 { get; set; }

    public Dense12<TLeaf>? P12 { get; set; }

    public Dense13<TLeaf>? P13 { get; set; }

    public Dense14<TLeaf>? P14 { get; set; }
}

public class Dense07<TLeaf>
{
    public string? Name { get; set; }

    public TLeaf? Leaf { get; set; }

    public Dense01<TLeaf>? P01 { get; set; }

    public Dense02<TLeaf>? P02 { get; set; }

    public Dense03<TLeaf>? P03 { get; set; }

    public Dense04<TLeaf>? P04 { get; set; }

    public Dense05<TLeaf>? P05 { get; set; }

    public Dense06<TLeaf>? P06 { get; set; }

    public Dense08<TLeaf>? P08 { get; set; }

    public Dense09<TLeaf>? P09 { get; set; }

    public Dense10<TLeaf>? P10 { get; set; }

    public Dense11<TLeaf>? P11 { get; set; }

    public Dense12<TLeaf>? P12 { get; set; }

    public Dense13<TLeaf>? P13 { get; set; }

    public Dense14<TLeaf>? P14 { get; set; }
}

public class Dense08<TLeaf>
{
    public string? Name { get; set; }

    public TLeaf? Leaf { get; set; }

    public Dense01<TLeaf>? P01 { get; set; }

    public Dense02<TLeaf>? P02 { get; set; }

    public Dense03<TLeaf>? P03 { get; set; }

    public Dense04<TLeaf>? P04 { get; set; }

    public Dense05<TLeaf>? P05 { get; set; }

    public Dense06<TLeaf>? P06 { get; set; }

    public Dense07<TLeaf>? P07 { get; set; }

    public Dense09<TLeaf>? P09 { get; set; }

    public Dense10<TLeaf>? P10 { get; set; }

    public Dense11<TLeaf>? P11 { get; set; }

    public Dense12<TLeaf>? P12 { get; set; }

    public Dense13<TLeaf>? P13 { get; set; }

    public Dense14<TLeaf>? P14 { get; set; }
}

public class Dense09<TLeaf>
{
    public string? Name { get; set; }

    public TLeaf? Leaf { get; set; }

    public Dense01<TLeaf>? P01 { get; set; }

    public Dense02<TLeaf>? P02 { get; set; }

    public Dense03<TLeaf>? P03 { get; set; }

    public Dense04<TLeaf>? P04 { get; set; }

    public Dense05<TLeaf>? P05 { get; set; }

    public Dense06<TLeaf>? P06 { get; set; }

    public Dense07<TLeaf>? P07 { get; set; }

    public Dense08<TLeaf>? P08 { get; set; }

    public Dense10<TLeaf>? P10 { get; set; }

    public Dense11<TLeaf>? P11 { get; set; }

    public Dense12<TLeaf>? P12 { get; set; }

    public Dense13<TLeaf>? P13 { get; set; }

    public Dense14<TLeaf>? P14 { get; set; }
}

public class Dense10<TLeaf>
{
    public string? Name { get; set; }

    public TLeaf? Leaf { get; set; }

    public Dense01<TLeaf>? P01 { get; set; }

    public Dense02<TLeaf>? P02 { get; set; }

    public Dense03<TLeaf>? P03 { get; set; }

    public Dense04<TLeaf>? P04 { get; set; }

    public Dense05<TLeaf>? P05 { get; set; }

    public Dense06<TLeaf>? P06 { get; set; }

    public Dense07<TLeaf>? P07 { get; set; }

    public Dense08<TLeaf>? P08 { get; set; }

    public Dense09<TLeaf>? P09 { get; set; }

    public Dense11<TLeaf>? P11 { get; set; }

    public Dense12<TLeaf>? P12 { get; set; }

    public Dense13<TLeaf>? P13 { get; set; }

    public Dense14<TLeaf>? P14 { get; set; }
}

public class Dense11<TLeaf>
{
    public string? Name { get; set; }

    public TLeaf? Leaf { get; set; }

    public Dense01<TLeaf>? P01 { get; set; }

    public Dense02<TLeaf>? P02 { get; set; }

    public Dense03<TLeaf>? P03 { get; set; }

    public Dense04<TLeaf>? P04 { get; set; }

    public Dense05<TLeaf>? P05 { get; set; }

    public Dense06<TLeaf>? P06 { get; set; }

    public Dense07<TLeaf>? P07 { get; set; }

    public Dense08<TLeaf>? P08 { get; set; }

    public Dense09<TLeaf>? P09 { get; set; }

    public Dense10<TLeaf>? P10 { get; set; }

    public Dense12<TLeaf>? P12 { get; set; }

    public Dense13<TLeaf>? P13 { get; set; }

    public Dense14<TLeaf>? P14 { get; set; }
}

public class Dense12<TLeaf>
{
    public string? Name { get; set; }

    public TLeaf? Leaf { get; set; }

    public Dense01<TLeaf>? P01 { get; set; }

    public Dense02<TLeaf>? P02 { get; set; }

    public Dense03<TLeaf>? P03 { get; set; }

    public Dense04<TLeaf>? P04 { get; set; }

    public Dense05<TLeaf>? P05 { get; set; }

    public Dense06<TLeaf>? P06 { get; set; }

    public Dense07<TLeaf>? P07 { get; set; }

    public Dense08<TLeaf>? P08 { get; set; }

    public Dense09<TLeaf>? P09 { get; set; }

    public Dense10<TLeaf>? P10 { get; set; }

    public Dense11<TLeaf>? P11 { get; set; }

    public Dense13<TLeaf>? P13 { get; set; }

    public Dense14<TLeaf>? P14 { get; set; }
}

public class Dense13<TLeaf>
{
    public string? Name { get; set; }

    public TLeaf? Leaf { get; set; }

    public Dense01<TLeaf>? P01 { get; set; }

    public Dense02<TLeaf>? P02 { get; set; }

    public Dense03<TLeaf>? P03 { get; set; }

    public Dense04<TLeaf>? P04 { get; set; }

    public Dense05<TLeaf>? P05 { get; set; }

    public Dense06<TLeaf>? P06 { get; set; }

    public Dense07<TLeaf>? P07 { get; set; }

    public Dense08<TLeaf>? P08 { get; set; }

    public Dense09<TLeaf>? P09 { get; set; }

    public Dense10<TLeaf>? P10 { get; set; }

    public Dense11<TLeaf>? P11 { get; set; }

    public Dense12<TLeaf>? P12 { get; set; }

    public Dense14<TLeaf>? P14 { get; set; }
}

public class Dense14<TLeaf>
{
    public string? Name { get; set; }

    public TLeaf? Leaf { get; set; }

    public Dense01<TLeaf>? P01 { get; set; }

    public Dense02<TLeaf>? P02 { get; set; }

    public Dense03<TLeaf>? P03 { get; set; }

    public Dense04<TLeaf>? P04 { get; set; }

    public Dense05<TLeaf>? P05 { get; set; }

    public Dense06<TLeaf>? P06 { get; set; }

    public Dense07<TLeaf>? P07 { get; set; }

    public Dense08<TLeaf>? P08 { get; set; }

    public Dense09<TLeaf>? P09 { get; set; }

    public Dense10<TLeaf>? P10 { get; set; }

    public Dense11<TLeaf>? P11 { get; set; }

    public Dense12<TLeaf>? P12 { get; set; }

    public Dense13<TLeaf>? P13 { get; set; }
}
