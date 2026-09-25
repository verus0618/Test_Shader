using UnityEngine;

namespace TestMisha.Slime
{
    /// <summary>
    /// Marks a string field to be edited as a tag popup, like a GameObject's own Tag field, instead of a plain
    /// text box. Rendered by Editor/TagSelectorDrawer.cs.
    /// </summary>
    public sealed class TagSelectorAttribute : PropertyAttribute
    {
    }
}
