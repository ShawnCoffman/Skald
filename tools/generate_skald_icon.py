"""Generate the Skald open-book Windows icon and a preview PNG."""

from pathlib import Path

from PIL import Image, ImageDraw


SIZE = 1024
ASSETS = Path(__file__).resolve().parents[1] / "src" / "Skald.App" / "Assets"


def polygon(draw, points, fill):
    draw.polygon([(int(x * SIZE), int(y * SIZE)) for x, y in points], fill=fill)


def line(draw, points, fill, width):
    draw.line([(int(x * SIZE), int(y * SIZE)) for x, y in points], fill=fill,
              width=int(width * SIZE), joint="curve")


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    image = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    draw.rounded_rectangle((24, 24, 1000, 1000), radius=205, fill="#102631")

    # A broad book silhouette reads clearly even in a 16 px taskbar icon.
    polygon(draw, [(0.12, 0.29), (0.27, 0.25), (0.42, 0.27), (0.50, 0.32),
                   (0.58, 0.27), (0.73, 0.25), (0.88, 0.29), (0.88, 0.76),
                   (0.73, 0.72), (0.59, 0.74), (0.50, 0.80), (0.41, 0.74),
                   (0.27, 0.72), (0.12, 0.76)], "#07171F")
    polygon(draw, [(0.15, 0.32), (0.29, 0.29), (0.41, 0.31), (0.48, 0.36),
                   (0.48, 0.73), (0.41, 0.69), (0.29, 0.67), (0.15, 0.71)], "#BFEAE4")
    polygon(draw, [(0.52, 0.36), (0.59, 0.31), (0.71, 0.29), (0.85, 0.32),
                   (0.85, 0.71), (0.71, 0.67), (0.59, 0.69), (0.52, 0.73)], "#E9F8ED")
    line(draw, [(0.50, 0.35), (0.50, 0.78)], "#30B8AF", 0.025)

    # The trace is the story captured across both pages; amber marks the incident.
    line(draw, [(0.21, 0.52), (0.32, 0.52), (0.38, 0.43), (0.45, 0.58),
                (0.54, 0.49), (0.65, 0.49), (0.70, 0.43), (0.79, 0.43)],
         "#087C83", 0.037)
    cx, cy, radius = int(0.70 * SIZE), int(0.43 * SIZE), int(0.034 * SIZE)
    draw.ellipse((cx - radius, cy - radius, cx + radius, cy + radius), fill="#FFBA55")

    image.save(ASSETS / "Skald.png")
    image.save(ASSETS / "Skald.ico", format="ICO", sizes=[
        (16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)
    ])


if __name__ == "__main__":
    main()
