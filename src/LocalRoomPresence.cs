using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ShipWalk
{
    // Keep only native room occupancy sensors available. Lights, renderers,
    // power, signal logic and intensity remain entirely under game control.
    internal sealed class LocalRoomPresence
    {
        private sealed class Sensor
        {
            internal Behaviour Controller;
            internal Collider Shape;
            internal bool Original, Overridden;
        }
        private readonly Dictionary<int, Sensor> sensors = new Dictionary<int, Sensor>();
        private readonly Build5150 map;
        private readonly Transform hull;
        private Light[] lights = new Light[0];
        private Component[] lightControls = new Component[0];
        private bool active;
        private float nextLightRefresh;
        public LocalRoomPresence(Build5150 map, Transform hull) { this.map = map; this.hull = hull; }
        public void Refresh(IEnumerable<Collider> colliders)
        {
            var found = new HashSet<int>();
            foreach (Collider shape in colliders)
            {
                if (shape == null || !shape.isTrigger) continue;
                var behaviour = shape.GetComponent(map.RoomLightType) as Behaviour;
                if (behaviour == null) continue;
                int id = shape.GetInstanceID(); found.Add(id);
                if (!sensors.ContainsKey(id)) sensors.Add(id, new Sensor
                    { Controller = behaviour, Shape = shape, Original = shape.enabled });
            }
            foreach (int id in sensors.Keys.Where(id => !found.Contains(id)).ToArray())
            { Restore(sensors[id]); sensors.Remove(id); }
            if (Time.realtimeSinceStartup >= nextLightRefresh)
            {
                lights = hull.GetComponentsInChildren<Light>(true);
                lightControls = hull.GetComponentsInChildren(map.LightControlType, true);
                nextLightRefresh = Time.realtimeSinceStartup + 5f;
            }
            SetActive(active);
        }
        public void SetActive(bool value)
        {
            active = value;
            foreach (Sensor sensor in sensors.Values)
            {
                if (active && sensor.Shape != null && sensor.Shape.isTrigger
                    && sensor.Controller != null && sensor.Controller.isActiveAndEnabled)
                {
                    sensor.Overridden = true;
                    if (!sensor.Shape.enabled) sensor.Shape.enabled = true;
                }
                else Restore(sensor);
            }
        }
        private static void Restore(Sensor sensor)
        {
            if (sensor.Overridden && sensor.Shape != null && sensor.Shape.enabled)
                sensor.Shape.enabled = sensor.Original;
            sensor.Overridden = false;
        }
        public void Restore() { SetActive(false); }
        public string Describe(Vector3 player)
        {
            int on = 0, near = 0, nearOn = 0;
            foreach (Light light in lights)
            {
                if (light == null) continue;
                bool enabled = light.isActiveAndEnabled && light.intensity > .001f;
                if (enabled) on++;
                if ((light.transform.position - player).sqrMagnitude > 35f * 35f) continue;
                near++; if (enabled) nearOn++;
            }
            int switchedOn = 0, powered = 0;
            foreach (Component light in lightControls)
            {
                if (light == null) continue;
                if (map.Bool(map.LightSwitchedOn, light)) switchedOn++;
                if (map.Bool(map.LightPowered, light)) powered++;
            }
            return "roomSensors=" + sensors.Count + "; roomSensorsActive="
                + sensors.Values.Count(s => s.Shape != null && s.Shape.enabled && s.Shape.gameObject.activeInHierarchy)
                + "; nativeLights=" + lights.Length + "; nativeLightsOn=" + on
                + "; nearbyLights=" + near + "; nearbyLightsOn=" + nearOn
                + "; lightControls=" + lightControls.Length + "; switchedOn=" + switchedOn + "; powered=" + powered;
        }
    }
}
