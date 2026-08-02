import { AppShell, Burger, Group, UnstyledButton, Text, Divider, Tooltip, Menu, useMantineColorScheme, useComputedColorScheme } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { NavLink, Outlet, useParams } from "react-router-dom";
import Logo from "../../components/logo/Logo";
import classes from "./AppLayout.module.css";
import { Icon, IconBox, IconChartBarPopular, IconChevronDown, IconChevronsLeft, IconChevronsRight, IconListDetails, IconLogout, IconMessageQuestion, IconMoon, IconNotes, IconPackageExport, IconProps, IconSectionSign, IconSun } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

interface Activity {
    id: string,
    name: string,
    hasRanking: boolean,
    hasQestions: boolean,
    hasRules: boolean,
}

const NavbarLink = (props: { label: string, collapsed: boolean, to: string, icon: React.ForwardRefExoticComponent<IconProps & React.RefAttributes<Icon>> }) => {
    return (
        <Tooltip label={props.label} disabled={!props.collapsed} position="right" openDelay={500}>
            <NavLink
                className={({ isActive }) => classes.link + " " + (isActive ? classes.active : "")}
                data-collapsed={props.collapsed || undefined}
                to={props.to}
                key={props.to}
            >
                <props.icon className={classes.linkIcon} stroke={1.5} />
                <span>{props.label}</span>
            </NavLink>
        </Tooltip>
    );
}

const ActivityNavbar = (props: { collapsed: boolean }) => {
    const { t } = useTranslation();
    const [currentActivity, setCurrentActivity] = useState<Activity | undefined>(undefined);
    const params = useParams();
    if (params.activityId && currentActivity?.id !== params.activityId) {
        setCurrentActivity({
            id: params.activityId,
            name: `The Best Programming Contest`,
            hasRanking: true,
            hasQestions: true,
            hasRules: true,
        });
    }
    if (!currentActivity) return;
    const links = [
        { to: `/activity/${currentActivity.id}/problems`, label: t("Problems"), icon: IconNotes },
        { to: `/activity/${currentActivity.id}/submit`, label: t("Submit"), icon: IconPackageExport },
        { to: `/activity/${currentActivity.id}/submissions`, label: t("My submissions"), icon: IconBox },
        currentActivity.hasRanking && { to: `/activity/${currentActivity.id}/ranking`, label: t("Ranking"), icon: IconChartBarPopular },
        currentActivity.hasQestions && { to: `/activity/${currentActivity.id}/questions`, label: t("Questions and announcements"), icon: IconMessageQuestion },
        currentActivity.hasRules && { to: `/activity/${currentActivity.id}/rules`, label: t("Rules"), icon: IconSectionSign },
    ]
    return (
        <>
            <Text className={classes.text}>{props.collapsed ? currentActivity.id : currentActivity.name}</Text>
            <Divider my="md" className={classes.divider} />
            {links.map(item => item && <NavbarLink key={item.to} to={item.to} label={item.label} icon={item.icon} collapsed={props.collapsed} />)}
            <Divider my="md" className={classes.divider} />
        </>
    );
}

const ColorSchemeSwitch = () => {
    const { setColorScheme } = useMantineColorScheme();
    const computedColorScheme = useComputedColorScheme('light', { getInitialValueInEffect: true });
    return (
        <UnstyledButton
            onClick={() => setColorScheme(computedColorScheme === 'light' ? 'dark' : 'light')}
            aria-label="Toggle color scheme"
        >
            <IconSun className={classes.color_schema_icon + " " + classes.light} stroke={1.5} />
            <IconMoon className={classes.color_schema_icon + " " + classes.dark} stroke={1.5} />
        </UnstyledButton>
    );
}

const LangSelector = () => {
    const { t, i18n } = useTranslation();
    return (
        <Menu trigger="hover" transitionProps={{ exitDuration: 0 }} withinPortal>
            <Menu.Target>
                <UnstyledButton>
                    {t("Lang")} <IconChevronDown size="0.9rem" stroke={1.5} />
                </UnstyledButton>
            </Menu.Target>
            <Menu.Dropdown>
                <Menu.Item onClick={() => i18n.changeLanguage("en")}>English</Menu.Item>
                <Menu.Item onClick={() => i18n.changeLanguage("pl")}>Polski</Menu.Item>
            </Menu.Dropdown>
        </Menu>
    );
}

const UserButton = (props: any) => {
    return (
        <UnstyledButton mx="xl" {...props} className={classes.user}>
            <Group>
                <div style={{ flex: 1 }}>
                    <Text size="sm" fw={500}>
                        John Smith
                    </Text>

                    <Text c="dimmed" size="xs">
                        john
                    </Text>
                </div>

                <IconChevronDown size={14} stroke={1.5} />
            </Group>
        </UnstyledButton>
    );
}

const UserMenu = () => {
    const { t } = useTranslation();
    return (
        <Menu shadow="md" width={200}>
            <Menu.Target>
                <UserButton />
            </Menu.Target>

            <Menu.Dropdown>
                <Menu.Item leftSection={<IconLogout size={14} />}>
                    {t("Logout")}
                </Menu.Item>
            </Menu.Dropdown>
        </Menu>
    );
}

export default function AppLayout() {
    const { t } = useTranslation();
    const [opened, { toggle }] = useDisclosure();
    const [collapsed, collapse] = useDisclosure();

    const CollapseButton =
        <>
            <Tooltip label={t("Expand")} disabled={!collapsed} position="right" openDelay={500}>
                <a
                    className={classes.link}
                    data-collapsed={collapsed || undefined}
                    href={''}
                    onClick={(event) => {
                        event.preventDefault();
                        collapse.toggle();
                    }}
                >
                    {collapsed ? <IconChevronsRight className={classes.linkIcon} stroke={1.5} /> : <IconChevronsLeft className={classes.linkIcon} stroke={1.5} />}
                    <span>{t("Collapse")}</span>
                </a>
            </Tooltip>
        </>

    return (
        <AppShell
            header={{ height: "4em" }}
            navbar={{
                width: collapsed ? 100 : 300,
                breakpoint: 'sm',
                collapsed: { mobile: !opened },
            }}
            padding="md"
        >
            <AppShell.Header className={classes.header}>
                <Group>
                    <Burger
                        opened={opened}
                        onClick={toggle}
                        hiddenFrom="sm"
                        size="sm"
                    />
                    <NavLink to="/"><Logo h="1em" mx="xl" /></NavLink>
                </Group>
                <Group>
                    <ColorSchemeSwitch />
                    <LangSelector />
                    <UserMenu />
                </Group>
            </AppShell.Header>

            <AppShell.Navbar p="md" className={classes.navbar}>
                <ActivityNavbar collapsed={collapsed} />
                <NavbarLink to={`/activities`} label={t("Activities")} icon={IconListDetails} collapsed={collapsed} />
                <Divider my="md" className={classes.divider} />
                {CollapseButton}
            </AppShell.Navbar>

            <AppShell.Main>
                <Outlet />
            </AppShell.Main>
        </AppShell>
    );
}
