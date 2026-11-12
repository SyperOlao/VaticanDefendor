from pathlib import Path
import math
import random

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as Plot
from matplotlib.patches import Circle, Polygon, Rectangle
from matplotlib.lines import Line2D


Profiles = [
    {"Name": "Открытый приход", "Stage": "Первый профиль", "Seed": 22,
     "Entries": [(-16.8, 0), (16.8, 0), (0, 16.8), (0, -16.8)],
     "Nodes": [(-4.8, 0), (7.2, 4.8), (-7.2, 7.2), (7.2, -7.2)], "Clusters": [(-12, -9.6), (12, 9.6), (-9.6, 12), (12, -12)]},
    {"Name": "Северный фронт", "Stage": "Отдельная проверка давления", "Seed": 81,
     "Entries": [(-4.8, 16.8), (4.8, 16.8)],
     "Nodes": [(-4.8, 0), (7.2, -7.2), (7.2, 2.4), (-7.2, -7.2)], "Clusters": [(-12, 9.6), (12, 12), (-12, -9.6), (12, -12)]},
    {"Name": "Два фланга", "Stage": "Дальнейший вариант", "Seed": 136,
     "Entries": [(-16.8, 2.4), (16.8, 2.4)],
     "Nodes": [(-4.8, 0), (-7.2, -7.2), (7.2, 4.8), (4.8, -7.2)], "Clusters": [(-9.6, 12), (9.6, 12), (-12, -12), (12, -12)]},
    {"Name": "Разомкнутая опушка", "Stage": "После проверки стен и разрывов", "Seed": 205,
     "Entries": [(-16.8, 2.4), (16.8, 2.4), (0, 16.8)],
     "Nodes": [(-4.8, 0), (7.2, 4.8), (-7.2, 7.2), (7.2, -7.2)], "Clusters": [(-12, 12), (12, 12), (0, -12), (-12, -9.6)]},
]


def ChooseObstacles(Profile):
    Generator = random.Random(Profile["Seed"])
    Candidates = [(Horizontal * 2.4, Vertical * 2.4) for Horizontal in range(-7, 8) for Vertical in range(-7, 8)
                  if math.dist((Horizontal * 2.4, Vertical * 2.4), (0, -3)) > 6
                  and all(math.dist((Horizontal * 2.4, Vertical * 2.4), Node) > 3.5 for Node in Profile["Nodes"])
                  and all(math.dist((Horizontal * 2.4, Vertical * 2.4), Entry) > 4.8 for Entry in Profile["Entries"])]
    Generator.shuffle(Candidates)
    Candidates.sort(key=lambda Position: min(math.dist(Position, Center) for Center in Profile["Clusters"]) + Generator.random())
    return Candidates[:18]


def DrawTree(Axis, Position, Scale):
    Horizontal, Vertical = Position
    Axis.add_patch(Rectangle((Horizontal - 0.18 * Scale, Vertical - 0.9 * Scale), 0.36 * Scale, 1.2 * Scale,
                             color="#866044", zorder=5))
    Axis.add_patch(Polygon([(Horizontal, Vertical + 1.2 * Scale), (Horizontal - 0.9 * Scale, Vertical - 0.6 * Scale),
                           (Horizontal + 0.9 * Scale, Vertical - 0.6 * Scale)], facecolor="#294f35", edgecolor="#203f2d", zorder=6))
    Axis.add_patch(Polygon([(Horizontal, Vertical + 1.2 * Scale), (Horizontal, Vertical - 0.6 * Scale),
                           (Horizontal + 0.9 * Scale, Vertical - 0.6 * Scale)], color="#355d3d", zorder=7))


def DrawProfile(Axis, Profile):
    Axis.set_facecolor("#738565")
    for Horizontal in range(-7, 8):
        for Vertical in range(-7, 8):
            if (Horizontal + Vertical) % 2 == 0:
                Axis.add_patch(Rectangle((Horizontal * 2.4 - 1.2, Vertical * 2.4 - 1.2), 2.4, 2.4, color="#7c8d6c", zorder=0))
    Axis.add_patch(Circle((0, -3), 5, facecolor="#98a785", edgecolor="#b3bda5", alpha=0.45, zorder=1))
    for Index, Position in enumerate(ChooseObstacles(Profile)):
        if Index % 4 == 0:
            Horizontal, Vertical = Position
            Axis.add_patch(Polygon([(Horizontal - 0.9, Vertical - 0.5), (Horizontal - 0.4, Vertical + 0.8),
                                   (Horizontal + 0.6, Vertical + 0.7), (Horizontal + 1, Vertical - 0.3),
                                   (Horizontal + 0.1, Vertical - 0.8)], facecolor="#8f9da7", edgecolor="#6f7c88", zorder=6))
        else:
            DrawTree(Axis, Position, 1)
    for Entry in Profile["Entries"]:
        Axis.add_patch(Rectangle((Entry[0] - 1.2, Entry[1] - 1.2), 2.4, 2.4, facecolor="#ab7958", edgecolor="#f4dac0", linewidth=1.2, zorder=3))
        Direction = (-Entry[0], -3 - Entry[1])
        Length = math.hypot(*Direction)
        Axis.arrow(Entry[0], Entry[1], Direction[0] / Length * 3.2, Direction[1] / Length * 3.2,
                   color="#d58e74", width=0.18, head_width=1, length_includes_head=True, zorder=4)
    for Index, Position in enumerate(Profile["Nodes"]):
        Color = ["#e3bc77", "#e3bc77", "#c3ccd0", "#8fb4d0"][Index]
        Axis.add_patch(Circle(Position, 1, facecolor=Color, edgecolor="#263b2b", linewidth=1.8, zorder=8))
        Axis.text(Position[0], Position[1] + 1.55, ["Д1", "Д2", "К", "Ж"][Index], ha="center", va="center", fontsize=9, color="#17281c", zorder=9)
    Axis.add_patch(Rectangle((-1.25, -4.9), 2.5, 3.8, facecolor="#b9b5a0", edgecolor="#35453a", zorder=10))
    Axis.add_patch(Polygon([(-1.7, -3), (0, -0.8), (1.7, -3), (0, -5.2)], facecolor="#b25c46", edgecolor="#783f33", zorder=11))
    Axis.text(0, -3, "+", color="#f6ebd0", fontsize=20, ha="center", va="center", zorder=12)
    Axis.add_patch(Circle((0, 0), 0.65, facecolor="#548bc8", edgecolor="#f8df8a", linewidth=1.7, zorder=13))
    Axis.set(xlim=(-18.6, 18.6), ylim=(-18.6, 18.6), aspect="equal")
    Axis.set_xticks([-16.8, 0, 16.8])
    Axis.set_yticks([-16.8, 0, 16.8])
    Axis.tick_params(labelsize=7, colors="#65706c")
    Axis.set_title(Profile["Name"] + "\n" + Profile["Stage"], fontsize=12, pad=12)


def Main():
    Output = Path(__file__).parent / "Results"
    Output.mkdir(exist_ok=True)
    Figure, Axes = Plot.subplots(1, 4, figsize=(19, 6.2))
    for Axis, Profile in zip(Axes, Profiles):
        DrawProfile(Axis, Profile)
    Figure.subplots_adjust(top=0.76, bottom=0.18, left=0.03, right=0.99, wspace=0.2)
    Figure.suptitle("Варианты давления на одной открытой арене. Концепт-схемы.", fontsize=14, y=0.95)
    Figure.text(0.5, 0.865, "Масштаб 36 × 36 м; церковь (0; −3), герой (0; 0); одинаковые четыре ресурсных узла.", ha="center", fontsize=11)
    Legend = [
        Line2D([0], [0], marker="o", color="none", markerfacecolor="#548bc8", markeredgecolor="#b99a48", label="Герой", markersize=8),
        Line2D([0], [0], marker="P", color="none", markerfacecolor="#b25c46", label="Церковь", markersize=9),
        Line2D([0], [0], marker="o", color="none", markerfacecolor="#e3bc77", label="Д1 / Д2 — дерево", markersize=8),
        Line2D([0], [0], marker="o", color="none", markerfacecolor="#c3ccd0", label="К — камень", markersize=8),
        Line2D([0], [0], marker="o", color="none", markerfacecolor="#8fb4d0", label="Ж — железо", markersize=8),
        Line2D([0], [0], marker="s", color="none", markerfacecolor="#ab7958", label="Вход: нельзя строить", markersize=8),
    ]
    Figure.legend(handles=Legend, loc="lower center", ncol=6, frameon=False, fontsize=9, bbox_to_anchor=(0.5, 0.065))
    Figure.text(0.5, 0.025, "Светлая область не исключает нападения. Стрелки обозначают входы, а не дороги. Различие строительных решений ещё не проверено.", ha="center", fontsize=9)
    Figure.savefig(Output / "ProfileConcepts.svg")
    Figure.savefig(Output / "ProfileConcepts.png", dpi=130)
    Plot.close(Figure)


if __name__ == "__main__":
    Main()
