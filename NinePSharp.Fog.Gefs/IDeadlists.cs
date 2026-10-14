namespace NinePSharp.Fog.Gefs;

// killblk: where a snapshot tree puts a block it no longer uses but an older snapshot may.
internal interface IDeadlists
{
    void Kill(Tree t, Bptr bp);
}
