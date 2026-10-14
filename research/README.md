# Exercise Physiology & Training Science Knowledge Base

This directory maintains the scientific foundation for the running coach engine and adaptive training algorithms in the `laufen` ecosystem. It tracks peer-reviewed meta-analyses, physiology reviews, and training intensity distribution (TID) studies to ensure all coaching decisions are evidence-based rather than driven by social media fitness dogmas.

---

## 📚 Document Index

| Document | Key Citations | Core Topics & Focus |
| :--- | :--- | :--- |
| [01. Zone 2 & Mitochondrial Adaptations](file:///home/berni/laufen/research/01_zone2_and_mitochondrial_adaptations_molmen_2025.md) | Mølmen et al. (*Sports Med*, 2025), Anvil (2026) | 353-study meta-regression on mitochondrial content across intensities; time efficiency of HIIT/SIT (1.7–3.9x); capillarization benefits of continuous base training. |
| [02. "Much Ado About Zone 2" Critical Review](file:///home/berni/laufen/research/02_much_ado_about_zone2_storoschuk_2025.md) | Storoschuk, Gibala, Gurd (*Sports Med*, 2025) | Deconstructing the low-intensity hype for general populations; the low-volume paradox (< 5 hrs/week); why higher intensities are essential for time-crunched runners. |
| [03. Training Intensity Distribution: Pyramidal vs. Polarized](file:///home/berni/laufen/research/03_training_intensity_distribution_pyramidal_vs_polarized.md) | Muniz-Pumares et al. (2025), Casado et al. (2025), Seiler (2010) | Polarized (80/20) vs. Pyramidal (~70/20/10); why recreational runners with 3 sessions/week thrive on controlled threshold & tempo work without autonomic burnout. |
| [04. Metabolic Flexibility, Testing & Commercial Zone 2 Models](file:///home/berni/laufen/research/04_metabolic_flexibility_and_testing_san_millan_and_athletics_health.md) | San-Millán & Brooks (2018), Kaufmann / AHI München (2026) | Fat oxidation, lactate shuttle kinetics, spirometry (VT1/LT1) vs. heart-rate formulas, commercial clinic perspectives, and injury prevention benefits. |

---

## 🎯 Architecture Translation: From Science to Training Engine

The scientific consensus from the 2025–2026 literature directly shapes the proposed **Adaptive 3-Pillar Hybrid Model** for recreational runners training 2 to 3 times per week:

```
┌────────────────────────────────────────────────────────────────────────┐
│                   THE 3-PILLAR PYRAMIDAL SPLIT                         │
├────────────────────────────────────────────────────────────────────────┤
│ Pillar 1: Aerobic Base & Capillarization Anchor (Z2)                   │
│   • Target: Capillary density growth (+13%), fat oxidation kinetics.   │
│   • Modality: Strictly heart-rate capped (< 158 bpm, LT1).             │
├────────────────────────────────────────────────────────────────────────┤
│ Pillar 2: Sub-Threshold & Cruise Intervals (Z3 / Tempo)                │
│   • Target: Lactate clearance, stroke volume, fatigue resistance.      │
│   • Modality: Controlled intervals near 165–174 bpm (below LTHR 178).  │
├────────────────────────────────────────────────────────────────────────┤
│ Pillar 3: Neuromuscular Speed & VO2max Stimulus (Intervals / Hills)    │
│   • Target: 1.7–3.9x mitochondrial signaling efficiency, cadence       │
│             enhancement (> 165 spm), running economy.                  │
│   • Modality: Short work reps (< 2 min) guided strictly by pace.       │
└────────────────────────────────────────────────────────────────────────┘
```

### Dynamic Modulation via Athlete Data
Rather than imposing a rigid static plan, the engine evaluates real-time biometrics:
1. **TSB (Training Stress Balance / Freshness):** Quality sessions (Pillars 2 and 3) are scheduled when TSB is positive/fresh ($+5$ to $+15$). Base recovery is mandated when fatigued.
2. **Cardiac Decoupling ($Pw:Hr$):** Verifies aerobic durability during Pillar 1 before expanding threshold volume in Pillar 2.
3. **Cadence Feedback:** Guards against overstriding and slow-cadence impact spikes during low-speed running.

---

## 🔗 Key Scientific References & Links

* **Mølmen et al. (2025)**: *Effects of Exercise Training on Mitochondrial and Capillary Growth in Human Skeletal Muscle: A Systematic Review and Meta-Regression*, Sports Medicine. [PubMed: 39390310](https://pubmed.ncbi.nlm.nih.gov/39390310/)
* **Storoschuk et al. (2025)**: *Much Ado About Zone 2: A Narrative Review Assessing the Efficacy of Zone 2 Training for Improving Mitochondrial Capacity and Cardiorespiratory Fitness in the General Population*, Sports Medicine. [PubMed: 40560504](https://pubmed.ncbi.nlm.nih.gov/40560504/)
* **San-Millán & Brooks (2018)**: *Assessment of Metabolic Flexibility by Means of Measuring Blood Lactate in High-Performance Athletes and Untrained Individuals*, Sports Medicine. [PubMed: 28623357](https://pubmed.ncbi.nlm.nih.gov/28623357/)
* **Muniz-Pumares et al. (2025)**: *Training Intensity Distribution in Long-Distance Runners: Pyramidal Versus Polarized Paradigms*, Sports Medicine / European Journal of Sport Science.
* **Anvil Discipline Magazine (2026)**: *Zone 2 und Mitochondrien: Was 353 Studien über Training mit niedriger Intensität sagen*. [Article Link](https://anvil.nikam.dev/blog/de/zona-2-i-mitokhondrii.html)
* **Athletics & Health Institut München (2026)**: *Zone-2-Training: Warum langsames Ausdauertraining der Schlüssel zu Mitochondrien und Longevity ist*. [Article Link](https://athleticsandhealth.com/wissen/zone-2-training/)
