# Eosin_VRRenderer for MMDShow and mmd2timeline Player

## Overview

This is an extended version of [Eosin's VRRenderer Plugin](https://hub.virtamate.com/resources/video-renderer-for-3d-vr180-vr360-and-flat-2d-audio-bvh-animation-recorder.11994/) for Virt-A-Mate. See that link for further information.

In order to better support MMD recording, I made some modifications previously. Later, I discovered [yunidatsu's Modified Version](https://github.com/yunidatsu/Eosin_VRRenderer), which includes a highly useful multi-threaded rendering feature. I forked that version and merged my previous changes with it, resulting in this version.

**This version is recommended to be used in conjunction with MMDShow (version 3.0.0 or higher) and mmd2timeline Player (version 1.5 or higher).**

## License

Eosin released the plugin under CC BY-SA.

## Credits

Credit for this plugin goes mainly to Eosin. Further credits from the original release:

* Thanks to **MacGruber** for his previous work which this plugin builds and heavily relies upon!
* Thanks to **Élie Michel** for his LilyRender360 shader which is responsible for the 15x performance gain compared to a CPU-based implementation!
* Thanks to **kuler** for contributing the correct method to do transparent render in VaM!
* Thanks to **ragingsimian**, **morkork**, **VAMguy**, **3115062**, **Cleo** and **Vezezepu** for improvement suggestions!

And

* Thanks to **yunidatsu** for developing the multi-threaded rendering feature!

## Changes in this version

Changes made on top of the merged base, listed by when they landed.

### 2026-10-07

**Forward Distance Limit.** A new slider sits directly beneath the *Camera Target* dropdown, ranged
0 to 10 (default 0). When a Camera Target is selected, it caps how far the containing atom may travel
forward along the motion source's forward axis, so the atom cannot pass through the model. Maximum
forward travel is the target's forward depth minus the limit — with the target 8 units ahead and a
limit of 0.1, travel stops at 7.9. If the motion source is already closer than the limit allows,
forward travel is zero at FOV 40 and below, while FOV above 40 retreats freely. Only forward motion is
constrained; the atom is never rotated to achieve the limit, and a target off to the side does not
restrain it. The *Camera Target* dropdown itself is unchanged and nothing is selected automatically.

**Motion Source and FOV Source dropdowns.** The Cam Forward feature can now take its reference objects
from a choice rather than hardcoded ones. *FOV Source* selects between the viewport camera and the
MMD plugin's FOV, defaulting to the plugin. *Motion Source* selects the viewport camera or any non-Person
atom in the scene, supplying base position and heading; the viewport camera is displayed as
"Main Camera". The atom list supports type-to-filter. Note that adding or removing atoms requires a
scene reload for the list to refresh.

**Cam Forward dolly.** The containing atom now tracks the motion source's position *and* rotation every
tick, rather than only adjusting forward distance. The dolly is pivoted on an absolute FOV of 40:
below 40 the atom advances, above 40 it retreats, with the distance determined by the *Cam Forward Zoom
In* / *Zoom Out Ratio* sliders and *Cam Forward Offset*. Because the pivot is absolute rather than
relative to a captured starting value, there is no stored baseline that can drift out of sync, and the
FOV and motion source can be switched mid-dolly without a jump.

**Plugin loading.** Fixed a `NullReferenceException` on load affecting the plugin's UI construction.

### 2026-10-02

**Cam Forward by FOV.** Introduced the feature that moves the containing atom forward or backward in
response to FOV changes from the mmd2timeline Player, in non-Flat render modes with Sync FOV enabled.
*Cam Forward Offset* was also corrected to track the control's position consistently.
