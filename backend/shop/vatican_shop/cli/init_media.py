"""Initialize the dedicated public-read item prefix using operator S3 credentials."""
from ..bootstrap.settings import Settings
from ..integrations.s3 import S3ImageStorage


def main():
    storage = S3ImageStorage(Settings.from_environment())
    storage.initialize()
    print("Shop image bucket is ready:", storage.settings.s3_bucket)


if __name__ == "__main__":
    main()
