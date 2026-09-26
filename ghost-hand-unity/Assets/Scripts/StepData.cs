using System;

// SHARED - agreed by all. Do not change without telling the group.
[Serializable]
public class Step
{
    public int id;
    public string part;     // "LED", "Resistor", "Capacitor"
    public string label;    // text shown on screen
    public float[] box;     // [x, y, w, h] in reference.jpg pixels
}

[Serializable]
public class StepList
{
    public Step[] steps;
}
