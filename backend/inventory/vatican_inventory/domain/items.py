import re


MAX_QUANTITY = 9_223_372_036_854_775_807


def item_id(value: object) -> str:
    if not isinstance(value, str) or re.fullmatch(r"[A-Za-z0-9_.:-]{1,160}", value) is None:
        raise ValueError("Invalid item identifier")
    return value


def consume_quantity(current: int, quantity: int) -> int | None:
    if type(quantity) is not int or not 1 <= quantity <= MAX_QUANTITY:
        raise ValueError("Invalid consumption quantity")
    return current - quantity if current >= quantity else None


def add_quantity(current: int, quantity: int) -> int:
    if type(quantity) is not int or quantity < 1 or current < 0 or current + quantity > MAX_QUANTITY:
        raise ValueError("Item capacity exceeded")
    return current + quantity
