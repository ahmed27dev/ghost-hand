// Shared by everyone. Matches StreamingAssets/steps.json.
// from/to are reference.jpg pixels, top-left origin.
//   point steps (LED):            from = long leg (anode), to = short leg (cathode)
//   arrow steps (resistor, wire): from = start, to = end
// fromText / toText are optional short tags shown next to the target holes.
using System;

[Serializable]
public class Step
{
    public int id;
    public string part;
    public string label;
    public string type;
    public float[] from;
    public float[] to;
    public string fromText;
    public string toText;
}

[Serializable]
public class StepList
{
    public Step[] steps;
}