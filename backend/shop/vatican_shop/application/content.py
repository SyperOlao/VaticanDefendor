from .catalog import MAX_OFFERS
from .access import administrator
from .errors import ApplicationError
from .ports import AuthGateway, ImageStorage, UnitOfWorkFactory, WeaponDefinitionsGateway
from ..domain.catalogs import build_catalog, validate_catalog, valid_offer_id


class ReadCatalog:
    def __init__(self, uow: UnitOfWorkFactory, definitions: WeaponDefinitionsGateway, images: ImageStorage | None = None):
        self.uow, self.definitions = uow, definitions
        self.images = images

    def get(self):
        with self.uow() as work:
            revision, offers = work.catalog.snapshot()
        definitions = self.definitions.get() if offers else []
        try:
            result = build_catalog({"version": "db-" + str(revision), "offers": offers}, definitions)
        except (ValueError, TypeError, KeyError):
            raise ApplicationError(503, "Shop catalog definitions are unavailable") from None
        for offer in result["offers"]:
            key = offer.get("imageKey")
            offer["imageUrl"] = self.images.url(key) if key and self.images else None
        return {**result, "revision": revision}


class AdminCatalog:
    def __init__(self, uow: UnitOfWorkFactory, auth: AuthGateway, definitions: WeaponDefinitionsGateway,
                 images: ImageStorage | None = None):
        self.uow, self.auth, self.definitions = uow, auth, definitions
        self.images = images

    def _authorize(self, token):
        return administrator(self.auth, token)

    def _response(self, row):
        result = row.response()
        key = result.get("imageKey")
        return {**result, "imageUrl": self.images.url(key) if key and self.images else None}

    def _image(self, content):
        key = content.get("imageKey")
        if key is not None:
            if self.images is None:
                raise ApplicationError(503, "Image storage is unavailable")
            self.images.require(key)

    @staticmethod
    def _content(content):
        try:
            catalog = validate_catalog({"version": "admin", "offers": [content]})
            return {"currencyId": "prestige", "available": True, "unavailableReason": "", "imageKey": None,
                    **catalog["offers"][0]}
        except (ValueError, TypeError):
            raise ApplicationError(422, "Invalid shop offer") from None

    def _known_weapons(self, contents):
        definitions = self.definitions.get()
        try:
            known = {item["Id"] for item in definitions}
        except (KeyError, TypeError):
            raise ApplicationError(503, "Game content is invalid") from None
        if any(content["weaponConfigurationId"] not in known for content in contents):
            raise ApplicationError(422, "Unknown weapon configuration")

    @staticmethod
    def _current(repo, offer_id, expected_revision):
        if type(expected_revision) is not int or expected_revision < 1:
            raise ApplicationError(422, "Invalid offer revision")
        row = repo.get(offer_id)
        if row is None or row.deleted_at is not None:
            raise ApplicationError(404, "Shop offer not found")
        if row.revision != expected_revision:
            raise ApplicationError(409, "Shop offer revision changed")
        return row

    def list(self, token, limit=50, after=None, include_deleted=False):
        self._authorize(token)
        if (type(limit) is not int or not 1 <= limit <= 100 or type(include_deleted) is not bool
                or after is not None and not valid_offer_id(after)):
            raise ApplicationError(422, "Invalid catalog pagination")
        with self.uow() as work:
            rows = work.catalog.list(limit + 1, after, include_deleted)
        return {"offers": [self._response(row) for row in rows[:limit]],
                "nextCursor": rows[limit - 1].content["id"] if len(rows) > limit else None}

    def get(self, token, offer_id):
        self._authorize(token)
        if not valid_offer_id(offer_id):
            raise ApplicationError(422, "Invalid shop offer ID")
        with self.uow() as work:
            row = work.catalog.get(offer_id)
            if row is None:
                raise ApplicationError(404, "Shop offer not found")
            result = row
        return self._response(result)

    def create(self, token, content):
        actor = self._authorize(token)
        content = self._content(content)
        self._known_weapons([content])
        self._image(content)
        with self.uow() as work:
            repo = work.catalog
            repo.revision(write=True)
            if repo.get(content["id"]) is not None:
                raise ApplicationError(409, "Shop offer ID already exists")
            if repo.count() >= MAX_OFFERS:
                raise ApplicationError(409, "Shop catalog limit reached")
            result = repo.create(content, actor)
        return self._response(result)

    def update(self, token, offer_id, expected_revision, content):
        actor = self._authorize(token)
        preserve_image = "imageKey" not in content
        content = self._content({**content, "id": offer_id})
        with self.uow() as work:
            previous = self._current(work.catalog, offer_id, expected_revision)
        if preserve_image:
            content["imageKey"] = previous.content.get("imageKey")
        if content["imageKey"] != previous.content.get("imageKey"):
            self._image(content)
        if content["weaponConfigurationId"] != previous.content["weaponConfigurationId"]:
            self._known_weapons([content])
        with self.uow() as work:
            repo = work.catalog
            repo.revision(write=True)
            previous = self._current(repo, offer_id, expected_revision)
            if content == previous.content:
                result = previous
            else:
                result = repo.update(content, actor)
        return self._response(result)

    def delete(self, token, offer_id, expected_revision):
        actor = self._authorize(token)
        if not valid_offer_id(offer_id):
            raise ApplicationError(422, "Invalid shop offer ID")
        with self.uow() as work:
            repo = work.catalog
            repo.revision(write=True)
            self._current(repo, offer_id, expected_revision)
            repo.delete(offer_id, actor)

    def import_catalog(self, token, catalog):
        actor = self._authorize(token)
        try:
            configured = validate_catalog(catalog)
        except (ValueError, TypeError):
            raise ApplicationError(422, "Invalid import catalog") from None
        contents = [self._content(offer) for offer in configured["offers"]]
        if contents:
            self._known_weapons(contents)
            for content in contents:
                self._image(content)
        created = 0
        with self.uow() as work:
            repo = work.catalog
            repo.revision(write=True)
            new = []
            for content in contents:
                prior = repo.get(content["id"])
                if prior is not None:
                    if prior.deleted_at is not None or prior.content != content:
                        raise ApplicationError(409, "Imported offer conflicts with existing catalog")
                else:
                    new.append(content)
            if repo.count() + len(new) > MAX_OFFERS:
                raise ApplicationError(409, "Shop catalog limit reached")
            for content in new:
                repo.create(content, actor)
                created += 1
        return {"created": created, "unchanged": len(contents) - created}
