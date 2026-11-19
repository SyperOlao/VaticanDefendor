"""Index the profile nickname lookup while preserving existing names."""
from alembic import op
import sqlalchemy as sa


revision = "0002_profile_name_lookup"
down_revision = "0001_initial"
branch_labels = None
depends_on = None


def upgrade():
    op.create_index("ix_profiles_display_name_lower", "profiles", [sa.text("lower(display_name)")])


def downgrade():
    op.drop_index("ix_profiles_display_name_lower", table_name="profiles")
