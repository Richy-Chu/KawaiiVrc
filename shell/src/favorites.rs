use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};

#[derive(Serialize, Deserialize, Clone, Debug, Default, PartialEq)]
pub struct Details {
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub thumbnail: String,
}

/// Details already fetched for favorite ids, so the pane never bursts the API.
#[derive(Serialize, Deserialize, Clone, Debug, Default)]
pub struct Store {
    #[serde(default)]
    pub avatars: BTreeMap<String, Details>,
    #[serde(default)]
    pub worlds: BTreeMap<String, Details>,
}

#[derive(Serialize, Clone, Debug, PartialEq)]
pub struct FavoriteEntry {
    pub id: String,
    pub kind: &'static str,
    pub name: String,
    pub thumbnail: String,
    pub cached: bool,
}

#[derive(Serialize, Debug)]
pub struct Favorites {
    pub avatars: Vec<FavoriteEntry>,
    pub worlds: Vec<FavoriteEntry>,
    pub pending: usize,
    pub total: usize,
}

pub fn path() -> PathBuf {
    crate::session::shell_dir().join("favorites.json")
}

pub fn load() -> Store {
    fs::read_to_string(path())
        .ok()
        .and_then(|raw| serde_json::from_str::<Store>(&raw).ok())
        .unwrap_or_default()
}

pub fn save(store: &Store) -> Result<(), String> {
    save_to(&path(), store)
}

fn save_to(target: &Path, store: &Store) -> Result<(), String> {
    let bytes = serde_json::to_vec_pretty(store).map_err(|error| error.to_string())?;
    if let Some(parent) = target.parent() {
        fs::create_dir_all(parent).map_err(|error| format!("{}: {error}", parent.display()))?;
    }
    fs::write(target, bytes).map_err(|error| format!("{}: {error}", target.display()))
}

fn string_list(value: Option<&Value>) -> Vec<String> {
    value
        .and_then(Value::as_array)
        .map(|rows| {
            rows.iter()
                .filter_map(|row| match row {
                    Value::String(id) => Some(id.trim().to_string()),
                    Value::Object(_) => row
                        .get("id")
                        .and_then(Value::as_str)
                        .map(|id| id.trim().to_string()),
                    _ => None,
                })
                .filter(|id| !id.is_empty())
                .collect()
        })
        .unwrap_or_default()
}

fn dedupe(ids: impl IntoIterator<Item = String>) -> Vec<String> {
    let mut seen = std::collections::BTreeSet::new();
    ids.into_iter()
        .filter(|id| seen.insert(id.clone()))
        .collect()
}

/// Favorite ids from the WPF app: its local avatar list plus the ids it keeps in
/// its settings.
pub fn ids(wpf_dir: &Path) -> (Vec<String>, Vec<String>) {
    let local = fs::read_to_string(wpf_dir.join("local-favorites.json"))
        .ok()
        .and_then(|raw| serde_json::from_str::<Value>(&raw).ok());
    let settings = fs::read_to_string(wpf_dir.join("settings.json"))
        .ok()
        .and_then(|raw| serde_json::from_str::<Value>(&raw).ok());

    let avatars = dedupe(
        string_list(local.as_ref())
            .into_iter()
            .chain(string_list(settings.as_ref().and_then(|value| value.get("favoriteAvatars")))),
    );
    let worlds = dedupe(string_list(
        settings.as_ref().and_then(|value| value.get("favoriteWorlds")),
    ));
    (avatars, worlds)
}

fn entries(
    ids: &[String],
    details: &BTreeMap<String, Details>,
    kind: &'static str,
) -> Vec<FavoriteEntry> {
    let mut rows: Vec<FavoriteEntry> = ids
        .iter()
        .map(|id| {
            let cached = details.get(id);
            FavoriteEntry {
                id: id.clone(),
                kind,
                name: cached.map(|details| details.name.clone()).unwrap_or_default(),
                thumbnail: cached
                    .map(|details| details.thumbnail.clone())
                    .unwrap_or_default(),
                cached: cached.is_some(),
            }
        })
        .collect();

    rows.sort_by(|left, right| {
        right
            .cached
            .cmp(&left.cached)
            .then_with(|| left.name.to_lowercase().cmp(&right.name.to_lowercase()))
            .then_with(|| left.id.cmp(&right.id))
    });
    rows
}

pub fn view(store: &Store, avatar_ids: &[String], world_ids: &[String]) -> Favorites {
    let avatars = entries(avatar_ids, &store.avatars, "avatar");
    let worlds = entries(world_ids, &store.worlds, "world");
    let pending = avatars.iter().filter(|entry| !entry.cached).count()
        + worlds.iter().filter(|entry| !entry.cached).count();
    Favorites {
        total: avatars.len() + worlds.len(),
        pending,
        avatars,
        worlds,
    }
}

/// The next ids to look up, at most `budget` of them, avatars first.
pub fn pending(
    store: &Store,
    avatar_ids: &[String],
    world_ids: &[String],
    budget: usize,
) -> (Vec<String>, Vec<String>) {
    let avatars: Vec<String> = avatar_ids
        .iter()
        .filter(|id| !store.avatars.contains_key(*id))
        .take(budget)
        .cloned()
        .collect();
    let worlds: Vec<String> = world_ids
        .iter()
        .filter(|id| !store.worlds.contains_key(*id))
        .take(budget - avatars.len())
        .cloned()
        .collect();
    (avatars, worlds)
}

pub fn apply(store: &mut Store, kind: &str, id: String, name: String, thumbnail: String) {
    let target = match kind {
        "world" => &mut store.worlds,
        _ => &mut store.avatars,
    };
    target.insert(id, Details { name, thumbnail });
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fixture_dir() -> PathBuf {
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("fixtures")
    }

    fn ids_fixture_dir() -> PathBuf {
        fixture_dir()
    }

    #[test]
    fn reads_and_dedupes_ids_from_both_wpf_files() {
        let (avatars, worlds) = ids(&ids_fixture_dir());
        assert_eq!(
            avatars,
            vec![
                "avtr_aaa",
                "avtr_bbb",
                "avtr_ccc",
                "avtr_dupe",
                "avtr_from_settings"
            ]
        );
        assert_eq!(worlds, vec!["wrld_one", "wrld_two"]);
    }

    #[test]
    fn missing_files_still_yield_empty_lists() {
        let (avatars, worlds) = ids(Path::new("Z:/nowhere"));
        assert!(avatars.is_empty());
        assert!(worlds.is_empty());
    }

    #[test]
    fn cached_entries_come_first_and_pending_is_counted() {
        let mut store = Store::default();
        store.avatars.insert(
            "avtr_bbb".to_string(),
            Details {
                name: "Beta".to_string(),
                thumbnail: "https://cdn/bbb.png".to_string(),
            },
        );
        let avatars = vec!["avtr_aaa".to_string(), "avtr_bbb".to_string()];
        let worlds = vec!["wrld_one".to_string()];

        let view = view(&store, &avatars, &worlds);
        assert_eq!(view.total, 3);
        assert_eq!(view.pending, 2);
        assert_eq!(view.avatars[0].id, "avtr_bbb");
        assert_eq!(view.avatars[0].name, "Beta");
        assert!(view.avatars[0].cached);
        assert_eq!(view.avatars[1].name, "");
        assert_eq!(view.worlds[0].kind, "world");
        assert!(!view.worlds[0].cached);
    }

    #[test]
    fn the_budget_is_shared_between_avatars_and_worlds() {
        let mut store = Store::default();
        store
            .avatars
            .insert("avtr_aaa".to_string(), Details::default());

        let avatars = vec!["avtr_aaa".to_string(), "avtr_bbb".to_string()];
        let worlds = vec!["wrld_one".to_string(), "wrld_two".to_string()];

        let (ask_avatars, ask_worlds) = pending(&store, &avatars, &worlds, 3);
        assert_eq!(ask_avatars, vec!["avtr_bbb"]);
        assert_eq!(ask_worlds, vec!["wrld_one", "wrld_two"]);
    }

    #[test]
    fn saved_details_round_trip() {
        let target = std::env::temp_dir().join(format!(
            "kawaii-shell-favorites-{}-{:?}.json",
            std::process::id(),
            std::thread::current().id()
        ));
        let mut store = Store::default();
        apply(
            &mut store,
            "world",
            "wrld_one".to_string(),
            "World One".to_string(),
            "https://cdn/world.png".to_string(),
        );
        save_to(&target, &store).expect("save");

        let loaded: Store =
            serde_json::from_slice(&fs::read(&target).expect("read")).expect("store json");
        assert_eq!(loaded.worlds["wrld_one"].name, "World One");
        assert_eq!(loaded.worlds["wrld_one"].thumbnail, "https://cdn/world.png");
        assert!(loaded.avatars.is_empty());

        let _ = fs::remove_file(target);
    }
}
