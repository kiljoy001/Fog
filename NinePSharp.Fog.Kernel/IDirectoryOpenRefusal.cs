namespace NinePSharp.Fog.Kernel;

// A device whose open refuses a directory opened for anything but reading in its own words, where
// devopen says Eperm; the namespace refuses such an open before the device sees it, and asks this.
internal interface IDirectoryOpenRefusal
{
    string Refusal(byte mode);
}
