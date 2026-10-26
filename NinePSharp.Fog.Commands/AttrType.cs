namespace NinePSharp.Fog.Commands;

// auth.h's attribute types: name=val must match, name? must be present, and, inside factotum,
// name=val must match if the name is present at all.
public enum AttrType
{
    Nameval,
    Query,
    Default,
}
