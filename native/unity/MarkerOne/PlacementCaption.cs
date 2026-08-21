using MarkerOne.Core;
using TMPro;
using UnityEngine;

namespace MarkerOne.Unity
{
    /// <summary>Who left it and when, facing the reader. Put it on a child of a
    /// placement prefab with a TextMeshPro component.</summary>
    public sealed class PlacementCaption : MonoBehaviour, IPlacedItemView
    {
        public TMP_Text Text;
        public Camera Viewer;

        private void Awake()
        {
            if (Text == null) { Text = GetComponent<TMP_Text>(); }
            if (Viewer == null) { Viewer = Camera.main; }
        }

        public void Bind(PlacedItem item)
        {
            if (Text == null) { return; }

            string who = string.IsNullOrWhiteSpace(item.Label) ? "" : item.Label.Trim();
            string when = When(item.CreatedAt);

            Text.text = who.Length > 0 && when.Length > 0 ? $"{who}  ·  {when}"
                : who.Length > 0 ? who
                : when;
        }

        private static string When(string iso)
        {
            return System.DateTimeOffset.TryParse(iso, out System.DateTimeOffset at)
                ? at.ToLocalTime().ToString("d MMM HH:mm")
                : "";
        }

        private void LateUpdate()
        {
            if (Viewer == null) { return; }

            // Yaw only. A caption that pitches to meet the camera reads as
            // falling over.
            Vector3 to = Viewer.transform.position - transform.position;
            to.y = 0;
            if (to.sqrMagnitude > 0.0001f) { transform.rotation = Quaternion.LookRotation(-to); }
        }
    }
}
