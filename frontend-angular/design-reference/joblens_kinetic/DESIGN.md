# Design System Strategy: The Intelligent Curator

## 1. Overview & Creative North Star
The North Star for this design system is **"The Intelligent Curator."** 

In the high-stakes world of enterprise recruiting, we must move beyond the "data-heavy dashboard" trope. Instead, we treat the interface as a premium editorial experience. This system balances the authoritative weight of deep indigo with the ethereal, fluid nature of AI insights. 

We break the "template" look through **Intentional Asymmetry** and **Tonal Depth**. By favoring whitespace over dividers and glassmorphism over rigid containers, we create a sense of "living software" that feels bespoke, premium, and human-centric.

---

## 2. Colors & Surface Logic

### Palette Application
*   **Primary (#00236f / #1e3a8a):** Used for structural authority. It anchors the sidebar and primary actions.
*   **Tertiary/Accent (#004a31 / #10b981):** Reserved exclusively for "AI-augmented" moments. When a user sees Emerald, they know the system is thinking for them.
*   **Neutral Grays (#f8f9fb to #e1e2e4):** These are not just "backgrounds"; they are the physical planes upon which data sits.

### The "No-Line" Rule
Standard 1px borders are prohibited for sectioning. Use **Background Shifts** to define boundaries. 
*   *Example:* A sidebar using `surface-container` against a main content area of `surface` creates a natural, sophisticated edge without a harsh line.

### Surface Hierarchy & Nesting
Treat the UI as stacked sheets of fine paper.
*   **Base:** `surface` (#f8f9fb)
*   **Lowered Sections:** `surface-container-low` (#f3f4f6) for secondary metadata areas.
*   **Elevated Components:** `surface-container-lowest` (#ffffff) for the primary interactive cards.
*   **The "Glass & Gradient" Rule:** Floating modals or dropdowns must use `surface-container-lowest` with an 80% opacity and a `20px` backdrop-blur. Apply a subtle linear gradient (Top-Left: `primary` at 5% opacity to Bottom-Right: transparent) to give surfaces a "soul."

---

## 3. Typography: The Editorial Scale

We pair **Manrope** (Display/Headlines) for its geometric, modern character with **Inter** (UI/Body) for its unparalleled legibility at small scales.

| Level | Token | Font | Size | Character |
| :--- | :--- | :--- | :--- | :--- |
| **Display** | `display-lg` | Manrope | 3.5rem | Bold, tight tracking (-2%). For hero stats. |
| **Headline** | `headline-md` | Manrope | 1.75rem | Semi-bold. For page headers and section titles. |
| **Title** | `title-md` | Inter | 1.125rem | Medium. For card titles and primary navigation. |
| **Body** | `body-md` | Inter | 0.875rem | Regular. The workhorse for all data. |
| **Label** | `label-sm` | Inter | 0.6875rem | Bold, All Caps, +5% tracking. For metadata/chips. |

---

## 4. Elevation & Depth

### The Layering Principle
Depth is achieved through **Tonal Layering** rather than structural shadows.
*   **Level 0:** `surface` (Background)
*   **Level 1:** `surface-container-lowest` (Cards) - Visual lift is created by the contrast of pure white against the soft gray background.

### Ambient Shadows
Where floating elements (like "AI Insight" popovers) require a shadow, use **Ambient Diffusion**:
*   `box-shadow: 0 12px 40px -10px rgba(0, 35, 111, 0.08);`
*   *Note:* The shadow is tinted with the `primary` color (#00236f) to avoid a "dirty" gray look.

### The "Ghost Border" Fallback
If high-contrast accessibility is required, use a **Ghost Border**:
*   `outline-variant` (#c5c5d3) at **15% opacity**. It should be felt, not seen.

---

## 5. Components

### Buttons & Actions
*   **Primary:** Solid `primary-container` (#1e3a8a) with `on-primary` (#ffffff) text. Use a `md` (0.75rem) corner radius.
*   **AI Action:** A subtle gradient from `tertiary` (#00311f) to `tertiary-container` (#004a31). This signifies "Generate with AI."
*   **Ghost/Tertiary:** No background. Use `primary` text. Transitions to `surface-container-high` on hover.

### Inputs & Fields
*   **Standard Input:** `surface-container-lowest` background. No border. On focus, apply a 2px `outline` using `primary` at 40% opacity.
*   **Error State:** Use `error` (#ba1a1a) for the label and a soft `error-container` tint for the background.

### Cards & Lists
*   **The Divider Forbiddance:** Never use `<hr>` or border-bottom. Separate list items using `12px` of vertical whitespace and a hover state shift to `surface-container-low`.
*   **Roundedness:** All cards must use `lg` (1.0rem) for a friendly, modern enterprise feel.

### AI Insight Chips (Specialty Component)
*   Used for "Match Score" or "Candidate Sentiment."
*   **Style:** Glassmorphic background (Emerald/Tertiary at 10% opacity) with a `4px` blur and high-contrast `on-tertiary-container` text.

---

## 6. Do’s and Don’ts

### Do:
*   **Do** use asymmetrical spacing. Give more "breath" (padding) to the top and left of a container than the bottom.
*   **Do** use Manrope for any numerical data. Its geometric figures feel more "premium tech."
*   **Do** nest cards within containers (e.g., White card on a Light Gray sidebar).

### Don't:
*   **Don't** use black (#000000) for text. Use `on-surface` (#191c1e) for better readability and a softer feel.
*   **Don't** use standard "drop shadows" with 0 blur. Shadows must always be large, soft, and atmospheric.
*   **Don't** clutter the sidebar with icons and text of the same weight. Use `label-md` for icons and `title-sm` for active text to create hierarchy.
*   **Don't** use 100% opaque borders to separate content. Let the background colors do the heavy lifting.