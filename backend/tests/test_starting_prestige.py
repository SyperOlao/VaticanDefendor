import psycopg
import pytest
from test_api import backend, bearer


@pytest.mark.parametrize("Registered", [False, True])
def test_new_account_starts_with_500_prestige(backend, Registered):
    Client, _, _ = backend
    if Registered:
        Session = Client.post("/v1/auth/register", json={"login":"prestige_start", "password":"test-password-123"}).json()
        Headers = bearer(Session["accessToken"])
    else:
        Session = Client.post("/v1/guests").json()
        Headers = bearer(Session["token"])
    assert Client.get("/v1/profile", headers=Headers).json()["permanentCurrency"] == 500
    assert Client.get("/v1/shop", headers=Headers).json()["walletBalance"] == 500


def test_linking_guest_and_logging_in_do_not_repeat_starting_prestige(backend):
    Client, Configuration, _ = backend
    Guest = Client.post("/v1/guests").json()
    with psycopg.connect(Configuration.database_url) as Database:
        Database.execute("UPDATE profiles SET permanent_currency=125,wallet_revision=wallet_revision+1 WHERE account_id=%s", (Guest["accountId"],))
    Credentials = {"login":"prestige_link", "password":"test-password-123"}
    Linked = Client.post("/v1/auth/register", headers=bearer(Guest["token"]), json=Credentials).json()
    assert Linked["accountId"] == Guest["accountId"]
    assert Client.get("/v1/profile", headers=bearer(Linked["accessToken"])).json()["permanentCurrency"] == 125
    LoggedIn = Client.post("/v1/auth/login", json=Credentials).json()
    assert Client.get("/v1/shop", headers=bearer(LoggedIn["accessToken"])).json()["walletBalance"] == 125
