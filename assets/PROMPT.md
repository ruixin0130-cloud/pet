# 素材说明

使用内置 imagegen 生成的灰白猫咪动作图。原参考图读取受本地沙箱初始化故障影响，因此根据对话中可见参考图描述角色特征后生成。

最终提示词：

Use case: stylized-concept. Asset type: production sprite atlas for a Windows desktop pet named 玉子. Generate a square 2048x2048 sprite sheet on a genuinely transparent alpha background (no checkerboard drawn in, no white backdrop), exactly 4 columns x 4 rows, 16 equal invisible square cells. Each cell contains exactly ONE complete same adorable fluffy gray-and-white baby kitten, delicate warm gray pencil outlines, soft watercolor shading, pink inner ears, blushing cheeks, huge shiny dark brown eyes with white highlights, white face blaze and chest, gray cap with a slightly asymmetric white blaze, gray fluffy tail tipped white. Chibi proportions, big head, short little legs, gentle Japanese stationery / picture-book look. Consistent character proportions and scale across every cell; full body and tail inside cell with generous 12% transparent padding; paws share a consistent low baseline. NO text, NO labels, NO grid lines, NO props, NO drop shadow or floor. Row 1 cells from left to right: front-facing standing idle eyes open; same idle eyes half shut; same idle eyes fully closed blink; same idle eyes open head tilted slightly. Row 2: side profile walking facing RIGHT, four sequential distinct walk-cycle poses (alternating extended and passing legs), same head shape and consistent silhouette scale. Row 3: side profile running facing RIGHT, four sequential distinct running-cycle poses (crouch, stretch airborne, reach forward, gather legs). Row 4: front-facing seated upright both front paws down; lying down belly on floor head up; sleeping curled lying flat eyes closed paws in front; joyful front-facing jumping forepaws raised. Accurate equal cell placement matters as these will be displayed individually by software. Preserve alpha transparency.

## 第三版互动素材

使用内置 imagegen，以 `tamago-sprites.png` 作为角色与画风参考生成 4×2 透明互动动作图。程序当前注册的互动动作是：好奇、玩毛线球、被摸、委屈、兴奋；图集中其余历史姿势仅作为素材存档，不会被界面或状态机触发。素材要求保留灰白猫咪、粉色耳朵、脸部白斑、暖灰铅笔线和柔和水彩质感，同时保留参考图里的小问号、毛线球、手掌等轻量装饰。

```text
Use case: stylized-concept. Asset type: transparent interaction sprite atlas for the same Windows desktop pet. Image 1 is the existing character and art style reference; preserve this exact fluffy gray-and-white kitten identity, proportions, warm pencil outline, soft watercolor shading, pink inner ears, blush, huge shiny dark brown eyes, white face blaze and chest, gray cap and fluffy tail. Generate a 2048x1024 image with genuinely transparent alpha, no checkerboard and no white background, exactly 4 columns x 2 rows, 8 equal invisible cells. No grid lines, no text, no labels, no watermark, no floor, no drop shadow. One complete cat per cell with generous transparent padding, same scale and baseline. Interaction sheet cell order left to right, top row: waving hello; curious with a small question mark; playing with a small pale pink yarn ball; being petted by one simple light skin-tone hand. Bottom row: happy with tiny pink hearts; pouty or wronged; surprised with a tiny exclamation mark; excited with one tiny pink sparkle. Keep all cell content fully inside its cell and preserve transparency.
```

## 第五版连续互动素材

使用内置 imagegen，以 `tamago-interactions.png` 为画风和角色参考生成 `tamago-interaction-animations.png`。这是一张 3×3 透明图集：第二行是摸头的靠近、轻抚和收尾；第三行是看毛线球、伸爪和拨球。第一行是历史素材，当前版本不会渲染或触发。

```text
Use case: illustration-story. Asset type: transparent PNG sprite atlas for a Windows desktop pet. Input image: the existing interaction atlas is a style and character reference only. Create one precise 3 columns by 3 rows animation atlas with nine evenly sized cells. Use the same fluffy grey-and-white kitten in every cell: round body, white face blaze and chest, brown eyes, pink ears and cheeks, soft hand-drawn pastel watercolor linework. Row 1 is reserved legacy material and is not rendered. Row 2: a gentle human hand approaches above the head; hand softly strokes the head while the kitten closes eyes; hand lifts away while the kitten keeps a content expression. Row 3: kitten watches a small pink yarn ball; reaches one paw toward it; taps it so the ball rolls slightly with one short curved motion line. Exactly nine equal cells in a uniform 3×3 grid; each cell has a full kitten centered with consistent scale and transparent padding. Fully transparent background, no shadows, no ground, no checkerboard, no labels, no numbers, no borders, no grid lines, no logo, no watermark, no extra characters.
```
