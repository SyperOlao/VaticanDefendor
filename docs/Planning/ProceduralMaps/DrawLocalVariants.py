from pathlib import Path
import math

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as Plot
from matplotlib.patches import Circle, Rectangle


def Main():
    Figure, Axes = Plot.subplots(1, 2, figsize=(12, 6.5))
    for Index, Axis in enumerate(Axes):
        Compact = Index == 0
        WoodFirst = (-4.8, 0)
        WoodSecond = (-7.2, 4.8)
        if not Compact:
            WoodSecond = (7.2, 4.8)
        Factory = (-4.8, 2.4)
        Axis.set_facecolor("#7c8d6c")
        Axis.add_patch(Circle(Factory, 5, facecolor="#ead4a3", edgecolor="#63512c", alpha=0.4, linestyle="--"))
        for NodeIndex, Node in enumerate([WoodFirst, WoodSecond]):
            Axis.plot([Factory[0], Node[0]], [Factory[1], Node[1]], color="#536250", linestyle=":", linewidth=1.5)
            Axis.add_patch(Circle(Node, 0.65, facecolor="#e3bc77", edgecolor="#3c3a21", zorder=5))
            Axis.text(Node[0], Node[1] + 1.15, "Д" + str(NodeIndex + 1) + ": 60", ha="center", fontsize=10)
        Axis.scatter(*Factory, marker="s", s=120, facecolor="#b5824f", edgecolor="#483625", zorder=6)
        Axis.text(Factory[0] - 1.1, Factory[1], "Лесопилка", ha="right", va="center", fontsize=10)
        Axis.scatter(0, 0, s=70, facecolor="#548bc8", edgecolor="#f8df8a", zorder=6)
        Axis.add_patch(Rectangle((-0.8, -4), 1.6, 2, facecolor="#b25c46", edgecolor="#783f33", zorder=5))
        Axis.text(0, -5.25, "Церковь", ha="center", fontsize=9)
        Axis.text(-9.9, -7.5, "Общий запас: 120 дерева\nПредел производства: строго менее 5 м", fontsize=10)
        if Compact:
            Description = "Д1: 2,4 м; Д2: " + str(round(math.dist(Factory, WoodSecond), 2)).replace(".", ",") + " м\nОдна выбранная позиция охватывает оба узла"
            Title = "Компактное хозяйство"
        else:
            Description = "Узлы разделяет " + str(round(math.dist(WoodFirst, WoodSecond), 2)).replace(".", ",") + " м > 10 м\nОбщей позиции в радиусе < 5 м от обоих нет"
            Title = "Разнесённое хозяйство"
        Axis.set_title(Title + "\n" + Description, fontsize=11)
        Axis.set(xlim=(-11, 11), ylim=(-9, 10), aspect="equal")
        Axis.set_xticks([-9.6, -4.8, 0, 4.8, 9.6])
        Axis.set_yticks([-7.2, -2.4, 2.4, 7.2])
        Axis.grid(alpha=0.2)
        Axis.tick_params(labelsize=8)
    Figure.subplots_adjust(top=0.75, bottom=0.14, left=0.055, right=0.98, wspace=0.15)
    Figure.suptitle("Один профиль: проверяемое отношение между узлами и производителем", fontsize=13, y=0.95)
    Figure.text(0.5, 0.87, "Остальные ресурсы, входы и боевые параметры при таком сравнении сохраняются.", ha="center", fontsize=10)
    Figure.text(0.5, 0.045, "Проверен охват ProductionSystem без волн и боя. Выгода в матче, геометрия постройки и защита ещё требуют проверки.", ha="center", fontsize=9)
    Output = Path(__file__).parent / "Results"
    Figure.savefig(Output / "LocalResourceVariants.svg")
    Figure.savefig(Output / "LocalResourceVariants.png", dpi=140)
    Plot.close(Figure)


if __name__ == "__main__":
    Main()
