using System;
using System.Collections.Generic;
using UnityEngine;

// One control point of a sounding. Values between points are interpolated linearly in height.
[Serializable]
public class SoundingLevel
{
    [Tooltip("Height above the ground (m).")] public float height;
    [Tooltip("Potential temperature (K).")] public float theta;
    [Tooltip("Water vapor mixing ratio (g/kg). If the air is supersaturated, cloud forms at the start.")] public float qv;
    [Tooltip("Wind along x (m/s).")] public float u;
    [Tooltip("Wind along z (m/s). (Meteorologists call this v.)")] public float v;

    public SoundingLevel(float height, float theta, float qv, float u, float v)
    {
        this.height = height; this.theta = theta; this.qv = qv; this.u = u; this.v = v;
    }
}

// A vertical profile of the background atmosphere (piecewise linear between control points).
[Serializable]
public class Sounding
{
    public List<SoundingLevel> levels = new List<SoundingLevel>();

    // theta (K), qv (kg/kg), u, v (m/s) at height z. Below the first / above the last point:
    // theta is extrapolated with the nearest segment's gradient (keeps the stability), the rest are held constant.
    public void Sample(double z, out double theta, out double qv, out double u, out double v)
    {
        theta = 300; qv = 0; u = 0; v = 0;
        int n = levels.Count;
        if (n == 0) return;
        if (n == 1) { var l = levels[0]; theta = l.theta; qv = l.qv / 1000.0; u = l.u; v = l.v; return; }
        int i = 0;
        while (i < n - 2 && z > levels[i + 1].height) i++;
        var a = levels[i]; var b = levels[i + 1];
        double h = Math.Max(b.height - a.height, 1e-6);
        double t = (z - a.height) / h;
        double tc = Math.Max(0, Math.Min(1, t));
        theta = a.theta + (b.theta - a.theta) * t;          // extrapolates
        qv = (a.qv + (b.qv - a.qv) * tc) / 1000.0;
        u = a.u + (b.u - a.u) * tc;
        v = a.v + (b.v - a.v) * tc;
    }

    public Sounding Clone() => JsonUtility.FromJson<Sounding>(JsonUtility.ToJson(this));

    // --- presets ---------------------------------------------------------------------------------

    // Fair-weather cumulus over land: well-mixed layer (theta 300 K, q_v 10.5 g/kg) to 1.5 km, capped by an
    // inversion of +5 K over 200 m, stable and dry above, weak shear. The mixed-layer air saturates at
    // ~1.54 km, just inside the inversion, so the start is cloud-free; surface heating and moistening make
    // thermals that reach saturation at the top of the mixed layer and form cumulus capped by the inversion.
    public static Sounding FairWeatherCumulus()
    {
        var s = new Sounding();
        s.levels.Add(new SoundingLevel(0, 300f, 10.5f, 3f, 0f));
        s.levels.Add(new SoundingLevel(1500, 300f, 10.5f, 4.5f, 0.5f));
        s.levels.Add(new SoundingLevel(1700, 305f, 5f, 4.7f, 0.6f));
        s.levels.Add(new SoundingLevel(3200, 311f, 3f, 6f, 1f));
        return s;
    }

    // Marine stratocumulus, after DYCOMS-II RF01 (Stevens et al. 2005): cool, moist well-mixed layer
    // (theta_l 289 K, q_t 9 g/kg) capped by a strong inversion at 840 m (+8.5 K, very dry above).
    // theta here is the liquid-water potential temperature; the initial saturation adjustment turns the
    // excess vapor into cloud and warms the air accordingly.
    public static Sounding Stratocumulus()
    {
        var s = new Sounding();
        s.levels.Add(new SoundingLevel(0, 289.0f, 9.0f, 7f, -5.5f));
        s.levels.Add(new SoundingLevel(840, 289.0f, 9.0f, 7f, -5.5f));
        s.levels.Add(new SoundingLevel(850, 297.5f + 2.15f, 1.5f, 7f, -5.5f));
        for (int z = 1000; z <= 2000; z += 250)
            s.levels.Add(new SoundingLevel(z, 297.5f + Mathf.Pow(z - 840, 1f / 3f), 1.5f, 7f, -5.5f));
        return s;
    }

    // Deep convection, after Weisman & Klemp (1982): theta from 300 K at the ground to 343 K at the
    // 12 km tropopause, relative humidity 1 - 0.75 (z/12km)^1.25 with q_v capped at 14 g/kg near the
    // ground, quarter-circle hodograph (5 m/s radius) up to 2 km, then straight to 25 m/s at 6 km.
    public static Sounding Cumulonimbus()
    {
        var s = new Sounding();
        const double ztr = 12000, th0 = 300, thTr = 343, tTr = 213, g = 9.81, cp = 1004, rd = 287;
        double exner = 1.0;   // ground at 1000 hPa
        double prevZ = 0, prevTh = th0;
        for (int z = 0; z <= 16000; z += 500)
        {
            double th = z <= ztr ? th0 + (thTr - th0) * Math.Pow(z / ztr, 1.25)
                                 : thTr * Math.Exp(g / (cp * tTr) * (z - ztr));
            if (z > 0) exner -= g / cp * (z - prevZ) * 0.5 * (1 / th + 1 / prevTh);   // dPi/dz = -g/(cp theta)
            prevZ = z; prevTh = th;
            double p = 1e5 * Math.Pow(exner, cp / rd), T = th * exner;
            double rh = z <= ztr ? 1 - 0.75 * Math.Pow(z / ztr, 1.25) : 0.25;
            double qv = Math.Min(rh * CloudSim.SatMixingRatio(T, p), 0.014);
            // hodograph
            double u, v;
            if (z <= 2000) { double a = Math.PI - 0.5 * Math.PI * z / 2000; u = 5 + 5 * Math.Cos(a); v = 5 * Math.Sin(a); }
            else if (z <= 6000) { u = 5 + 20 * (z - 2000) / 4000.0; v = 5; }
            else { u = 25; v = 5; }
            // move with the storm: subtract a typical storm motion so it stays near the middle
            u -= 10; v -= 3;
            s.levels.Add(new SoundingLevel(z, (float)th, (float)(1000 * qv), (float)u, (float)v));
        }
        return s;
    }

    // Stable layer for gravity waves: theta +6 K/km (N ~ 0.014 1/s), fairly dry, 10 m/s wind.
    public static Sounding StableLayer()
    {
        var s = new Sounding();
        s.levels.Add(new SoundingLevel(0, 295f, 6f, 10f, 0f));
        s.levels.Add(new SoundingLevel(10000, 355f, 0.5f, 10f, 0f));
        return s;
    }
}
