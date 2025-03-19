import { Card, Stack, Title, Text, Group, Pagination } from "@mantine/core";
import { IconSchool, IconTrophy } from "@tabler/icons-react";
import classes from "./ActivitiesPage.module.css"
import { useNavigate } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { useState } from "react";

interface Activity {
    id: string,
    type: string,
    isActive: boolean,
    name: string,
    props: {key: string, value: string}[]
}

const data: Activity[] = [
    {
        id: "PCN1",
        type: "contest",
        isActive: true,
        name: "Programming Contest No 1",
        props: []
    },
    {
        id: "PC1",
        type: "course",
        isActive: true,
        name: "Programming course",
        props: [
            {
                key: "Group",
                value: "LA"
            },
            {
                key: "Teacher",
                value: "John Smith"
            },
        ]
    },
    {
        id: "PCN2",
        type: "contest",
        isActive: true,
        name: "Programming Contest No 2",
        props: []
    },
    {
        id: "PCN3",
        type: "contest",
        isActive: false,
        name: "Programming Contest No 3",
        props: []
    },
    {
        id: "APC",
        type: "course",
        isActive: true,
        name: "Advanced programming course",
        props: []
    },
    {
        id: "PCN4",
        type: "contest",
        isActive: true,
        name: "Programming Contest No 4",
        props: []
    },
    {
        id: "PCN5",
        type: "contest",
        isActive: false,
        name: "Programming Contest No 5",
        props: []
    },
    {
        id: "APC2",
        type: "course",
        isActive: true,
        name: "Advanced programming course II",
        props: []
    },
];

const MAX_ITEMS_PER_PAGE = 5;

const getIcon = (type: string) => {
    switch (type) {
        case "contest":
            return <IconTrophy size="3em" />;
        case "course":
            return <IconSchool size="3em" />;
        default:
            return <IconSchool size="3em" />;
    }
}

export default function ActivitiesPage() {
    const { t } = useTranslation();
    const navigate = useNavigate();
    const [page, setPage] = useState<number>(1);
    const pages = Math.ceil(data.length / MAX_ITEMS_PER_PAGE);
    const firstItem = MAX_ITEMS_PER_PAGE * (page - 1);
    const list = data.slice(firstItem, firstItem + MAX_ITEMS_PER_PAGE).map(item =>
        <Card className={classes.item + " " + (item.isActive && classes.active)} onClick={() => navigate(`/activity/${item.id}/problems`)}>
            <Group justify="space-between">
                <Group>
                    {getIcon(item.type)}<Text size="lg">{item.name}</Text>
                </Group>
                <Stack justify="flex-end" gap={0} className={classes.stack}>
                    {item.props.map(p => <Text>{p.key}: {p.value}</Text>)}
                </Stack>
            </Group>
        </Card>
    );
    return (
        <>
            <Title>{t("Activities")}</Title>
            {list}
            <Group justify="center" mt="xl">
                <Pagination total={pages} value={page} onChange={setPage} mx="auto" />
            </Group>
        </>
    );
}
